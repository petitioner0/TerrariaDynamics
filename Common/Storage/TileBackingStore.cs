using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using Terraria;

namespace TerrariaDynamics.Common.Storage;

/// <summary>
/// Version-specific adapter for tML's global, pinned TileData&lt;T&gt; arrays.
/// Never constructs another Tilemap: its constructor would erase existing tiles.
/// Only the physical WIDTH changes; Height (the TileId stride) stays constant.
/// Must run on the game thread, outside rendering/world generation/world saving.
/// </summary>
internal static partial class TileBackingStore
{
    private const BindingFlags StaticFields = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
    private static readonly Type DataType = typeof(Tile).Assembly.GetType("Terraria.TileData", true);
    private static readonly FieldInfo SetLengthEvent = RequiredField(DataType, "OnSetLength");
    private static readonly FieldInfo LengthField = RequiredField(DataType, "<Length>k__BackingField");
    private static readonly FieldInfo WidthField = typeof(Tilemap).GetField(nameof(Tilemap.Width));

    public static void EnsureWidth(ref Tilemap map, int requiredWidth)
    {
        if (requiredWidth <= map.Width)
            return;
        if (requiredWidth > short.MaxValue + 1 || requiredWidth < 1)
            throw new ArgumentOutOfRangeException(nameof(requiredWidth));

        int capacity = Math.Min(short.MaxValue + 1, (requiredWidth + 255) / 256 * 256);
        Resize(ref map, capacity);
    }

    // Also used to release the extension when unloading the mod.
    public static void Resize(ref Tilemap map, int width)
    {
        if (width == map.Width)
            return;
        if (width < 1 || width > short.MaxValue + 1 || map.Height == 0)
            throw new ArgumentOutOfRangeException(nameof(width));

        uint oldLength = (uint)LengthField.GetValue(null);
        int newLength = checked(width * map.Height);
        object resizedMap = map;
        WidthField.SetValue(resizedMap, checked((ushort)width));
        var prepared = new List<BufferSwap>();
        bool committed = false;
        try {
            // OnSetLength is the registry of INITIALIZED buffers, including other mods'
            // ITileData. New types initialized later use TileData.Length automatically.
            foreach (Type owner in InitializedBuffers())
                prepared.Add(new BufferSwap(owner, newLength));

            // All allocations, copying, pinning and field validation finish BEFORE any
            // live pointer changes. Old arrays stay pinned until the entire commit ends.
            try {
                foreach (BufferSwap swap in prepared)
                    swap.Install();
                LengthField.SetValue(null, (uint)newLength);
                map = (Tilemap)resizedMap;
                committed = true;
            }
            catch {
                foreach (BufferSwap swap in prepared)
                    swap.Restore();
                LengthField.SetValue(null, oldLength);
                throw;
            }
        }
        finally {
            foreach (BufferSwap swap in prepared)
                swap.Release(committed);
        }
    }

    private static FieldInfo RequiredField(Type type, string name) =>
        type.GetField(name, StaticFields)
        ?? throw new NotSupportedException($"Unsupported tModLoader Tile storage: {type.FullName}.{name}");

    private static HashSet<Type> InitializedBuffers()
    {
        var owners = new HashSet<Type>();
        var handlers = (Delegate)SetLengthEvent.GetValue(null);
        if (handlers != null) {
            foreach (Delegate handler in handlers.GetInvocationList()) {
                Type owner = handler.Method.DeclaringType;
                if (owner == null || !owner.IsGenericType
                    || owner.GetGenericTypeDefinition().FullName != "Terraria.TileData`1")
                    throw new NotSupportedException("Unrecognized tModLoader TileData resize callback.");
                owners.Add(owner);
            }
        }
        return owners;
    }

    private sealed class BufferSwap
    {
        private readonly FieldInfo dataField;
        private readonly FieldInfo pointerField;
        private readonly FieldInfo handleField;
        private readonly Array oldData;
        private readonly object oldPointer;
        private readonly GCHandle oldHandle;
        private readonly Array newData;
        private readonly object newPointer;
        private GCHandle newHandle;

        public BufferSwap(Type owner, int length) : this(owner, length,
            (source, target) => Array.Copy(source, target, Math.Min(source.Length, target.Length)))
        {
        }

        public BufferSwap(Type owner, int length, Action<Array, Array> copy)
        {
            dataField = RequiredField(owner, "<data>k__BackingField");
            pointerField = RequiredField(owner, "<ptr>k__BackingField");
            handleField = RequiredField(owner, "handle");
            oldData = (Array)dataField.GetValue(null);
            oldPointer = pointerField.GetValue(null);
            oldHandle = (GCHandle)handleField.GetValue(null);
            newData = Array.CreateInstance(owner.GenericTypeArguments[0], length);
            copy?.Invoke(oldData, newData);
            newHandle = GCHandle.Alloc(newData, GCHandleType.Pinned);
            // Reflection accepts IntPtr for pointer fields on .NET 8. Avoid requiring
            // /unsafe so the in-game tML source compiler can build this mod as well.
            newPointer = newHandle.AddrOfPinnedObject();
        }

        public void Install()
        {
            dataField.SetValue(null, newData);
            pointerField.SetValue(null, newPointer);
            handleField.SetValue(null, newHandle);
        }

        public void Restore()
        {
            dataField.SetValue(null, oldData);
            pointerField.SetValue(null, oldPointer);
            handleField.SetValue(null, oldHandle);
        }

        public void Release(bool committed)
        {
            if (committed) {
                if (oldHandle.IsAllocated)
                    oldHandle.Free();
            }
            else if (newHandle.IsAllocated)
                newHandle.Free();
        }
    }
}
