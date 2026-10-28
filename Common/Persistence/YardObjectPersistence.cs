using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Microsoft.Xna.Framework;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;
using Terraria;
using Terraria.IO;
using TerrariaDynamics.Common.Storage;
using TerrariaDynamics.Common.TileYards;
using TerrariaDynamics.Common.WorldEditing;

namespace TerrariaDynamics.Common.Persistence;

// Keep the original chest/sign serializers, including tML's ItemIO extension.
// Only adapt their assumptions that all object coordinates are in the world.
internal sealed class YardObjectPersistence : IDisposable
{
    private readonly Func<IEnumerable<Yard>> getYards;
    private readonly List<ILHook> hooks = new();
    private delegate Tile SignTileReader(ref Tilemap map, int x, int y);

    public YardObjectPersistence(Func<IEnumerable<Yard>> getYards)
    {
        this.getYards = getYards;
        try {
            hooks.Add(new ILHook(typeof(WorldFile).GetMethod(nameof(WorldFile.SaveChests)), PatchSaveChests));
            hooks.Add(new ILHook(typeof(WorldFile).GetMethod(nameof(WorldFile.LoadSigns)), PatchLoadSigns));
            MethodInfo validate = typeof(WorldFile).Assembly.GetType("Terraria.ModLoader.IO.WorldIO", true)
                .GetMethod("ValidateSigns", BindingFlags.Public | BindingFlags.Static)
                ?? throw new NotSupportedException("WorldIO.ValidateSigns was not found.");
            hooks.Add(new ILHook(validate, PatchValidateSigns));
        }
        catch {
            Dispose();
            throw;
        }
    }

    private void PatchSaveChests(ILContext il)
    {
        var cursor = new ILCursor(il);
        int chestLocal = -1;
        if (!cursor.TryGotoNext(MoveType.After, i => i.MatchLdsfld<Main>(nameof(Main.chest)),
            i => i.MatchLdloc(out _), i => i.OpCode == OpCodes.Ldelem_Ref,
            i => i.MatchStloc(out chestLocal)))
            throw new NotSupportedException("Unable to locate SaveChests chest lookup.");
        if (!cursor.TryGotoNext(MoveType.Before, i => i.MatchBrfalse(out _),
            i => i.MatchLdsfld<Main>(nameof(Main.chest)), i => i.MatchLdloc(out _),
            i => i.OpCode == OpCodes.Ldnull, i => i.OpCode == OpCodes.Stelem_Ref))
            throw new NotSupportedException("Unable to locate SaveChests invalid chest cleanup.");
        // The original invalid flag is on the stack. Suppress deletion only for
        // registered Yard objects; the original count and item writer stay intact.
        cursor.Emit(OpCodes.Ldloc, chestLocal);
        cursor.EmitDelegate<Func<bool, Chest, bool>>((invalid, chest) =>
            invalid && FindOwner(chest.x, chest.y) == null);
    }

    private static void PatchLoadSigns(ILContext il)
    {
        var cursor = new ILCursor(il);
        if (!cursor.TryGotoNext(MoveType.Before, i => i.MatchCall<Tilemap>("get_Item"),
            i => i.OpCode == OpCodes.Pop))
            throw new NotSupportedException("Unable to locate LoadSigns unused Tile lookup.");
        cursor.Remove();
        cursor.EmitDelegate<SignTileReader>(ReadSignTile);
    }

    private static Tile ReadSignTile(ref Tilemap map, int x, int y)
    {
        // LoadSigns precedes ModSystem.LoadWorldData and storage expansion. This
        // value is discarded by vanilla; defer only valid storage coordinates.
        return IsStorageCoordinate(x, y) ? default : map[x, y];
    }

    private static void PatchValidateSigns(ILContext il)
    {
        var cursor = new ILCursor(il);
        if (!cursor.TryGotoNext(MoveType.After, i => i.MatchLdsfld<Main>(nameof(Main.sign)),
            i => i.MatchLdloc(out _), i => i.OpCode == OpCodes.Ldelem_Ref))
            throw new NotSupportedException("Unable to locate ValidateSigns sign lookup.");
        if (!cursor.Next.MatchBrfalse(out _))
            throw new NotSupportedException("Unrecognized ValidateSigns null check.");
        // Only the null-test sees null; Main.sign is never modified. Validate
        // deferred entries after Yard metadata and snapshots have been restored.
        cursor.EmitDelegate<Func<Sign, Sign>>(sign =>
            sign != null && IsStorageCoordinate(sign.x, sign.y) ? null : sign);
    }

    private static bool IsStorageCoordinate(int x, int y) =>
        x >= Math.Max(Main.maxTilesX, YardStorageAllocator.StartX)
        && x < YardStorageAllocator.CoordinateLimit && y >= 0
        && y < Math.Min((int)Main.tile.Height, YardStorageAllocator.CoordinateLimit);

    private Yard FindOwner(int x, int y)
    {
        foreach (Yard yard in getYards())
            if (yard.IsValid && yard.Bounds.Contains(x, y)) return yard;
        return null;
    }

    public void ValidateLoadedObjects()
    {
        foreach (Chest chest in Main.chest) {
            if (chest != null && chest.x >= Main.maxTilesX)
                Validate(chest.x, chest.y, chest: true);
        }
        foreach (Sign sign in Main.sign) {
            if (sign != null && sign.x >= Main.maxTilesX)
                Validate(sign.x, sign.y, chest: false);
        }
    }

    private void Validate(int x, int y, bool chest)
    {
        Yard yard = FindOwner(x, y);
        if (yard == null)
            throw new InvalidDataException($"No Yard owns the saved {(chest ? "chest" : "sign")} at ({x}, {y}).");
        // Missing content must not destroy the vanilla records or their payloads.
        if (!yard.IsLoaded) return;
        Rectangle bounds = YardObjectTransfer.ObjectBounds(Main.tile, x, y, chest);
        if (!yard.Bounds.Contains(bounds))
            throw new InvalidDataException("Saved container crosses its Yard boundary.");
        for (int tx = bounds.Left; tx < bounds.Right; tx++) {
            for (int ty = bounds.Top; ty < bounds.Bottom; ty++) {
                Tile tile = Main.tile[tx, ty];
                if (!tile.HasTile || !(chest ? Main.tileContainer[tile.TileType] : Main.tileSign[tile.TileType]))
                    throw new InvalidDataException("Saved container has no matching Yard tiles.");
            }
        }
    }

    public void Dispose()
    {
        for (int i = hooks.Count - 1; i >= 0; i--) hooks[i].Dispose();
        hooks.Clear();
    }
}
