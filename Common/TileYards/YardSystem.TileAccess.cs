using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.Xna.Framework;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;
using Terraria;
using Terraria.DataStructures;
using Terraria.ID;
using TerrariaDynamics.Common.Storage;

namespace TerrariaDynamics.Common.TileYards;

public sealed partial class YardSystem
{
    private readonly List<ILHook> tileAccessHooks = new();
    private Yard interactionYard;
    private delegate Tile TileReader(ref Tilemap map, int x, int y);
    private delegate void TileWriter(ref Tilemap map, int x, int y, Tile tile);
    private delegate Tile PointTileReader(ref Tilemap map, Point point);
    private delegate Tile Point16TileReader(ref Tilemap map, Point16 point);

    private void LoadTileAccess()
    {
        try {
            On_WorldGen.InWorld += StorageInWorld;
            On_WorldGen.UpdateMapTile += UpdateStorageMap;
            HookTileAccess(typeof(Player), "TileInteractionsMouseOver", "TileInteractionsUse");
            HookTileAccess(typeof(Chest), "IsLocked", "Unlock", "Lock", "FindChest", "FindChestByGuessing",
                "CreateChest", "CanDestroyChest", "DestroyChest", "DestroyChestDirect");
            HookTileAccess(typeof(Sign), "ReadSign", "TextSign", "KillSign");
            HookTileAccess(typeof(Framing), "WallFrame", "GetTileSafely");
            HookTileAccess(typeof(Wiring), "UpdateMech", "HitSwitch", "Actuate", "ActuateForced",
                "XferWater", "TripWire", "PixelBoxPass", "CheckLogicGate", "HitWire", "HitWireSingle",
                "ToggleHangingLantern", "Toggle2x2Light", "ToggleLampPost", "ToggleLamp",
                "ToggleChandelier", "ToggleCampFire", "ToggleFirePlace", "GeyserTrap", "DeActive", "ReActive");
            HookTileAccess(typeof(WorldGen), "TileFrame", "PlaceTile", "KillTile", "CanKillTile",
                "RangeFrame", "SquareTileFrame", "SquareWallFrame", "PlaceChestDirect", "PlaceDresserDirect",
                "CheckTileBreakability", "CheckSign", "PlaceSign", "OpenDoor", "CloseDoor", "UnlockDoor",
                "Check1xX", "Check2xX", "Check1x2", "CheckOnTable1x1", "Check3x3Wall", "Check2x3Wall",
                "Check3x2Wall", "Check4x3Wall", "Check6x4Wall", "Check1x1", "CheckGolf1x1", "CheckLogicTiles",
                "CheckAlch", "CheckBanner", "CheckWeaponsRack", "CheckMan", "CheckWoman", "Check1x2Top",
                "CheckPile", "Check2x1", "Check4x2", "Check2x2", "CheckBoulderChest", "Check3x1",
                "Check3x2", "Check3x4", "Check5x4", "Check6x3", "CheckCannon", "CheckMB", "Check2x2Style",
                "CheckChand", "Check3x3", "Check2x5", "Check3x5", "Check3x6", "CheckSunflower", "CheckDye",
                "CheckGnome", "CheckPot", "CheckChest", "CheckFoodPlatter", "CheckTorch",
                "CheckProjectilePressurePad", "CheckDoorOpen", "CheckDoorClosed", "ShiftTrapdoor", "ShiftTallGate",
                "SwitchMB", "SwitchFountain", "SwitchMonolith", "SwitchCannon", "ToggleGemLock", "IsChestRigged",
                "LaunchRocket", "LaunchRocketSmall", "ShootFromCannon");
        }
        catch {
            UnloadTileAccess();
            throw;
        }
    }

    private void UnloadTileAccess()
    {
        On_WorldGen.InWorld -= StorageInWorld;
        On_WorldGen.UpdateMapTile -= UpdateStorageMap;
        foreach (ILHook hook in tileAccessHooks)
            hook.Dispose();
        tileAccessHooks.Clear();
    }

    private void HookTileAccess(Type type, params string[] names)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        foreach (string name in names) {
            MethodInfo[] methods = type.GetMethods(flags).Where(method => method.Name == name).ToArray();
            if (methods.Length == 0)
                throw new NotSupportedException($"Unsupported tModLoader tile helper: {type.Name}.{name}.");
            foreach (MethodInfo method in methods)
                tileAccessHooks.Add(new ILHook(method, il => {
                    if ((type == typeof(Chest) || type == typeof(Sign)) && method.GetParameters().Length >= 2
                        && method.GetParameters()[0].ParameterType == typeof(int)
                        && method.GetParameters()[1].ParameterType == typeof(int))
                        PatchObjectOwnership(il, method.ReturnType);
                    if (type == typeof(WorldGen) && (name is "TileFrame" or "PlaceTile" or "KillTile"
                        || name == "CanKillTile" && method.GetParameters().Last().ParameterType == typeof(bool).MakeByRefType()))
                        PatchInlineTileBounds(il, localBounds: false);
                    else if (type == typeof(WorldGen) && name is "CheckSign" or "PlaceSign")
                        PatchInlineTileBounds(il, localBounds: true);
                    else if (type == typeof(Framing) && name == "WallFrame")
                        PatchInlineTileBounds(il, localBounds: false);
                    else if (type == typeof(Wiring) && name == "HitWire")
                        PatchWireBounds(il);
                    else if (type == typeof(Wiring) && name == "CheckLogicGate")
                        PatchLogicGateLimit(il);
                    PatchNativeTileReads(il);
                }));
        }
    }

    private bool StorageInWorld(On_WorldGen.orig_InWorld orig, int x, int y, int fluff)
    {
        // A native action may only visit its own Yard, including at local Y=0.
        // Outside an action, only registered, loaded storage rectangles qualify.
        if (interactionYard != null)
            return interactionYard.Bounds.Contains(x, y);
        return orig(x, y, fluff) || TryStorageYard(x, y, out _);
    }

    private bool UpdateStorageMap(On_WorldGen.orig_UpdateMapTile orig, int x, int y, bool addToList) =>
        x < Main.maxTilesX && orig(x, y, addToList);

    // Preserve every vanilla branch and its local initialization. Add a second
    // successful path only for a loaded Yard; never enlarge Main.maxTilesX/Y.
    private void PatchInlineTileBounds(ILContext il, bool localBounds)
    {
        var cursor = new ILCursor(il);
        if (!cursor.TryGotoNext(MoveType.After, i => i.MatchLdsfld<Main>(nameof(Main.maxTilesY))))
            throw new NotSupportedException($"Missing tile boundary in {il.Method.FullName}.");
        if (!cursor.TryGotoNext(MoveType.Before, i => i.OpCode.FlowControl == FlowControl.Cond_Branch))
            throw new NotSupportedException($"Missing tile boundary branch in {il.Method.FullName}.");
        Instruction branch = cursor.Next;
        bool branchesToSuccess = branch.OpCode.Code is Code.Blt or Code.Blt_S or Code.Ble or Code.Ble_S;
        ILLabel target = branchesToSuccess ? BranchLabel(cursor, branch) : cursor.DefineLabel();
        if (!branchesToSuccess)
            target.Target = branch.Next;
        cursor.Index = 0;
        if (!cursor.TryGotoNext(MoveType.Before,
            i => localBounds ? i.MatchLdloc(0) : i.MatchLdarg(0),
            i => i.MatchLdcI4(out _), i => i.OpCode.FlowControl == FlowControl.Cond_Branch))
            throw new NotSupportedException($"Missing tile boundary start in {il.Method.FullName}.");
        // TileFrame starts its try block at this instruction. Include the new
        // guard in that block; branching from outside into it produces invalid IL.
        cursor.MoveAfterLabels(intoEHRanges: true);
        cursor.Emit(OpCodes.Ldarg_0);
        cursor.Emit(OpCodes.Ldarg_1);
        cursor.EmitDelegate<Func<int, int, bool>>(IsInteractionStorageCell);
        cursor.Emit(OpCodes.Brtrue, target);
    }

    private bool IsInteractionStorageCell(int x, int y) => interactionYard != null
        && interactionYard.Bounds.Contains(x, y);

    private void PatchWireBounds(ILContext il)
    {
        var cursor = new ILCursor(il);
        for (int axis = 0; axis < 2; axis++) {
            bool vertical = axis == 1;
            if (!cursor.TryGotoNext(MoveType.Before, i => i.MatchLdloc(out _),
                i => i.MatchLdcI4(2), i => i.OpCode.Code is Code.Blt or Code.Blt_S))
                throw new NotSupportedException("Unsupported Wiring.HitWire lower bound.");
            cursor.Index += 2;
            ReplaceWireBranch(cursor, (value, limit) => interactionYard == null ? value < limit
                : value < (vertical ? interactionYard.Bounds.Top : interactionYard.Bounds.Left));
            string field = vertical ? nameof(Main.maxTilesY) : nameof(Main.maxTilesX);
            if (!cursor.TryGotoNext(MoveType.Before, i => i.MatchLdsfld<Main>(field),
                i => i.MatchLdcI4(2), i => i.OpCode == OpCodes.Sub,
                i => i.OpCode.Code is Code.Bge or Code.Bge_S))
                throw new NotSupportedException("Unsupported Wiring.HitWire upper bound.");
            cursor.Index += 3;
            ReplaceWireBranch(cursor, (value, limit) => interactionYard == null ? value >= limit
                : value >= (vertical ? interactionYard.Bounds.Bottom : interactionYard.Bounds.Right));
        }
    }

    private static void ReplaceWireBranch(ILCursor cursor, Func<int, int, bool> rejected)
    {
        ILLabel skip = BranchLabel(cursor, cursor.Next);
        cursor.Remove();
        cursor.EmitDelegate(rejected);
        cursor.Emit(OpCodes.Brtrue, skip);
    }

    private static ILLabel BranchLabel(ILCursor cursor, Instruction branch)
    {
        // ILContext normalizes branch operands to ILLabel before invoking hooks.
        // Reuse that label so other edits can continue moving its target safely.
        if (branch.Operand is ILLabel label)
            return label;
        if (branch.Operand is not Instruction target)
            throw new NotSupportedException($"Unsupported tile boundary target: {branch.Operand}.");
        ILLabel converted = cursor.DefineLabel();
        converted.Target = target;
        return converted;
    }

    private void PatchLogicGateLimit(ILContext il)
    {
        var cursor = new ILCursor(il);
        if (!cursor.TryGotoNext(MoveType.After, i => i.MatchLdsfld<Main>(nameof(Main.maxTilesY))))
            throw new NotSupportedException("Unsupported Wiring.CheckLogicGate upper bound.");
        cursor.EmitDelegate<Func<int, int>>(limit => interactionYard?.Bounds.Bottom ?? limit);
    }

    private void PatchNativeTileReads(ILContext il)
    {
        var cursor = new ILCursor(il);
        while (cursor.TryGotoNext(MoveType.Before, i => i.MatchCall<Tilemap>("get_Item")
            && ((Mono.Cecil.MethodReference)i.Operand).Parameters.Count == 2)) {
            cursor.Remove();
            cursor.EmitDelegate<TileReader>(ReadInteractionTile);
        }
        cursor.Index = 0;
        while (cursor.TryGotoNext(MoveType.Before, i => i.MatchCall<Tilemap>("get_Item")
            && ((Mono.Cecil.MethodReference)i.Operand).Parameters.Count == 1)) {
            string parameter = ((Mono.Cecil.MethodReference)cursor.Next.Operand).Parameters[0].ParameterType.FullName;
            cursor.Remove();
            if (parameter == typeof(Point).FullName)
                cursor.EmitDelegate<PointTileReader>((ref Tilemap map, Point point) => ReadInteractionTile(ref map, point.X, point.Y));
            else if (parameter == typeof(Point16).FullName)
                cursor.EmitDelegate<Point16TileReader>((ref Tilemap map, Point16 point) => ReadInteractionTile(ref map, point.X, point.Y));
            else
                throw new NotSupportedException($"Unsupported Tilemap index in {il.Method.FullName}.");
        }
        cursor.Index = 0;
        while (cursor.TryGotoNext(MoveType.Before, i => i.MatchCall<Tilemap>("set_Item"))) {
            cursor.Remove();
            cursor.EmitDelegate<TileWriter>(WriteInteractionTile);
        }
    }

    private void PatchObjectOwnership(ILContext il, Type returnType)
    {
        var cursor = new ILCursor(il);
        ILLabel original = cursor.DefineLabel();
        original.Target = cursor.Next;
        cursor.Emit(OpCodes.Ldarg_0);
        cursor.Emit(OpCodes.Ldarg_1);
        cursor.EmitDelegate<Func<int, int, bool>>((x, y) => interactionYard != null && !interactionYard.Bounds.Contains(x, y));
        cursor.Emit(OpCodes.Brfalse, original);
        if (returnType == typeof(int))
            cursor.Emit(OpCodes.Ldc_I4_M1);
        else if (returnType == typeof(bool))
            cursor.Emit(OpCodes.Ldc_I4_0);
        else if (returnType != typeof(void))
            throw new NotSupportedException($"Unsupported Yard object helper result in {il.Method.FullName}.");
        cursor.Emit(OpCodes.Ret);
    }

    private Tile ReadInteractionTile(ref Tilemap map, int x, int y)
    {
        if (interactionYard == null)
            return map[x, y];
        if (interactionYard.Bounds.Contains(x, y)) {
            Tile result = map[x, y];
            interactionChanges?.Observe(x, y, result);
            return result;
        }
        Point air = YardStorageAllocator.ReservedTile.Value;
        Tile tile = map[air.X, air.Y];
        tile.ClearEverything();
        return tile;
    }

    private void WriteInteractionTile(ref Tilemap map, int x, int y, Tile tile)
    {
        if (interactionYard == null || interactionYard.Bounds.Contains(x, y)) {
            if (interactionYard != null)
                interactionChanges?.Observe(x, y, map[x, y]);
            map[x, y].CopyFrom(tile);
        }
    }

    private void PrepareInteractionAirTile()
    {
        if (YardStorageAllocator.ReservedTile is not Point air || regions.Values.Any(yard => yard.Bounds.Contains(air))) {
            if (Main.netMode == NetmodeID.MultiplayerClient)
                throw new InvalidOperationException("Missing authoritative Yard neighbor-read address.");
            YardStorageAllocator.ReservedTile = null;
            if (!YardStorageAllocator.TryAllocate(new Point(1, 1), regions.Values.Select(yard => yard.Bounds),
                Main.maxTilesX, Main.tile.Height, out air))
                throw new InvalidOperationException("No storage address is available for native Yard neighbor queries.");
            TileBackingStore.EnsureWidth(ref Main.tile, air.X + 1);
            YardStorageAllocator.ReservedTile = air;
        }
        Main.tile[air.X, air.Y].ClearEverything();
    }

}
