using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using Microsoft.Xna.Framework;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using Terraria;
using Terraria.DataStructures;
using Terraria.ID;
using Terraria.Localization;
using Terraria.ObjectData;
using TerrariaDynamics.Common.WorldEditing;

namespace TerrariaDynamics.Common.TileYards;

public sealed partial class YardSystem
{
    private YardTileChanges interactionChanges;
    private bool receivingTilePacket;

    private void LoadTileNetworking()
    {
        IL_NetMessage.SendData += PatchTileSquareBounds;
        IL_MessageBuffer.GetData += PatchNativeTileReads;
        On_NetMessage.SendData += SendInteractionData;
        On_RemoteClient.SectionRange += YardSectionRange;
        On_MessageBuffer.GetData += ReceiveInteractionData;
    }

    private void UnloadTileNetworking()
    {
        On_MessageBuffer.GetData -= ReceiveInteractionData;
        On_RemoteClient.SectionRange -= YardSectionRange;
        On_NetMessage.SendData -= SendInteractionData;
        IL_MessageBuffer.GetData -= PatchNativeTileReads;
        IL_NetMessage.SendData -= PatchTileSquareBounds;
    }

    private void PatchTileSquareBounds(ILContext il)
    {
        var cursor = new ILCursor(il);
        int x = -1, y = -1, width = -1, height = -1;
        if (!cursor.TryGotoNext(MoveType.After,
            i => i.MatchLdarg(4), i => i.MatchStloc(out x),
            i => i.MatchLdarg(5), i => i.OpCode == OpCodes.Conv_I4, i => i.MatchStloc(out y),
            i => i.MatchLdarg(6), i => i.OpCode == OpCodes.Conv_I4, i => i.MatchStloc(out width))
            || !cursor.TryGotoNext(MoveType.After,
                i => i.MatchLdarg(7), i => i.OpCode == OpCodes.Conv_I4, i => i.MatchStloc(out height)))
            throw new NotSupportedException("Unsupported native TileSquare bounds locals.");
        int start = cursor.Index;
        if (!cursor.TryGotoNext(MoveType.Before, i => i.MatchLdloc(3), i => i.MatchLdloc(x),
            i => i.OpCode == OpCodes.Conv_I2, i => i.MatchCallvirt<BinaryWriter>(nameof(BinaryWriter.Write))))
            throw new NotSupportedException("Unsupported native TileSquare bounds end.");
        ILLabel write = cursor.DefineLabel();
        write.Target = cursor.Next;
        cursor.Index = start;
        cursor.Emit(OpCodes.Ldloc, x);
        cursor.Emit(OpCodes.Ldloc, y);
        cursor.Emit(OpCodes.Ldloc, width);
        cursor.Emit(OpCodes.Ldloc, height);
        cursor.EmitDelegate<Func<int, int, int, int, bool>>(IsStorageTileSquare);
        cursor.Emit(OpCodes.Brtrue, write);
    }

    private bool IsStorageTileSquare(int x, int y, int width, int height) => width > 0 && height > 0
        && width <= byte.MaxValue && height <= byte.MaxValue
        && TryStorageYard(x, y, out Yard yard) && yard.Bounds.Contains(new Rectangle(x, y, width, height));

    private bool YardSectionRange(On_RemoteClient.orig_SectionRange orig, RemoteClient client,
        int size, int firstX, int firstY) => TryStorageYard(firstX, firstY, out _)
        || firstX < Main.maxTilesX && orig(client, size, firstX, firstY);

    private void SendInteractionData(On_NetMessage.orig_SendData orig, int msgType, int remoteClient,
        int ignoreClient, NetworkText text, int number, float number2, float number3, float number4,
        int number5, int number6, int number7)
    {
        if (msgType == MessageID.TileSquare) {
            Rectangle area = new(number, (int)number2, (int)number3, (int)number4);
            Yard yard = interactionYard;
            if (yard != null) {
                if (!yard.Bounds.Intersects(area))
                    return;
                area = Rectangle.Intersect(area, yard.Bounds);
            }
            else if (!TryStorageYard(area.X, area.Y, out yard)) {
                if (area.X >= Main.maxTilesX)
                    return;
                orig(msgType, remoteClient, ignoreClient, text, number, number2, number3, number4,
                    number5, number6, number7);
                return;
            }
            else
                area = Rectangle.Intersect(area, yard.Bounds);
            if (area.IsEmpty || receivingTilePacket)
                return;
            // Native sizes are bytes and every packet must fit the network buffer.
            for (int x = area.Left; x < area.Right; x += 32)
                for (int y = area.Top; y < area.Bottom; y += 32) {
                    Rectangle chunk = new(x, y, Math.Min(32, area.Right - x), Math.Min(32, area.Bottom - y));
                    interactionChanges?.Observe(chunk);
                    orig(msgType, remoteClient, ignoreClient, text, x, y, chunk.Width, chunk.Height,
                        number5, number6, number7);
                    // A directed reply does not cover the other clients.
                    if (Main.netMode == NetmodeID.Server && remoteClient == -1 && ignoreClient == -1)
                        interactionChanges?.Sent(chunk);
                }
            return;
        }
        if (TryOutgoingTileCell(msgType, number, number2, number3, number4, out Point cell)) {
            if (interactionYard != null && !interactionYard.Bounds.Contains(cell)
                || receivingTilePacket)
                return;
            if (cell.X >= Main.maxTilesX) {
                if (!TryStorageYard(cell.X, cell.Y, out Yard yard))
                    return;
                if (Main.netMode == NetmodeID.Server && interactionChanges != null
                    && msgType != MessageID.TemporaryAnimation) {
                    TileObjectData data = TileObjectData.GetTileData(Main.tile[cell.X, cell.Y]);
                    Point anchor = data == null ? cell : TileObjectData.TopLeft(cell.X, cell.Y).ToPoint();
                    Rectangle affected = new(anchor.X, anchor.Y, data?.Width ?? 1, data?.Height ?? 1);
                    interactionChanges.Command(Rectangle.Intersect(affected, yard.Bounds));
                }
            }
        }
        orig(msgType, remoteClient, ignoreClient, text, number, number2, number3, number4,
            number5, number6, number7);
    }

    private static bool TryOutgoingTileCell(int message, int number, float number2, float number3,
        float number4, out Point cell)
    {
        cell = default;
        switch (message) {
            case MessageID.TileManipulation:
            case MessageID.ToggleDoorState:
            case MessageID.ChestUpdates:
                cell = new Point((int)number2, (int)number3);
                return true;
            case MessageID.LockAndUnlock:
            case MessageID.TemporaryAnimation:
                cell = new Point((int)number3, (int)number4);
                return true;
            case MessageID.HitSwitch:
            case MessageID.PaintTile:
            case MessageID.PaintWall:
            case MessageID.GemLockToggle:
                cell = new Point(number, (int)number2);
                return true;
            default:
                return false;
        }
    }

    private void ReceiveInteractionData(On_MessageBuffer.orig_GetData orig, MessageBuffer buffer,
        int start, int length, out int messageType)
    {
        messageType = buffer.readBuffer[start];
        ReadOnlySpan<byte> payload = buffer.readBuffer.AsSpan(start + 1, length - 1);
        if (!TryIncomingTileArea(messageType, payload, out Rectangle area)) {
            orig(buffer, start, length, out messageType);
            return;
        }
        if (Main.netMode == NetmodeID.Server && InvalidObjectRequest(messageType, payload, area))
            return;
        if (area.X < Main.maxTilesX) {
            // Physical backing-store expansion must not legalize a world packet
            // whose rectangle crosses into storage.
            if (messageType == MessageID.TileSquare && area.Right > Main.maxTilesX)
                return;
            orig(buffer, start, length, out messageType);
            return;
        }
        if (!TryStorageYard(area.X, area.Y, out Yard yard) || !yard.Bounds.Contains(area)) {
            if (Main.netMode == NetmodeID.MultiplayerClient && IsSynchronized)
                RequestSnapshot();
            return;
        }
        bool receiving = Main.netMode == NetmodeID.MultiplayerClient;
        if (receiving && !IsSynchronized)
            return;
        if (!receiving) {
            // Tile mutations arrive through the validated Yard interaction request.
            // Vanilla chest/sign UI requests still use their original packet path.
            if (messageType is not (MessageID.RequestChestOpen or MessageID.RequestReadSign
                or MessageID.ReadSign or MessageID.SyncPlayerChest or MessageID.ChestName)
                || (uint)buffer.whoAmI >= Main.maxPlayers)
                return;
            Player player = Main.player[buffer.whoAmI];
            if (!player.active || player.dead || player.ghost
                || !InYardReach(player, yard, area.Location, TileReachCheckSettings.Simple)
                || messageType == MessageID.RequestChestOpen && LockedContainer(area.Location))
                return;
            if (messageType == MessageID.SyncPlayerChest
                && BinaryPrimitives.ReadInt16LittleEndian(payload) != player.chest)
                return;
        }
        int receivedType = messageType;
        try {
            RunStorageAction(yard, _ => {
                interactionChanges.Observe(area);
                orig(buffer, start, length, out receivedType);
            }, receiving);
        }
        catch (Exception error) {
            Mod.Logger.Warn($"Could not apply Yard tile packet {messageType}.", error);
            if (receiving)
                RequestSnapshot();
        }
        messageType = receivedType;
    }

    private static bool TryIncomingTileArea(int message, ReadOnlySpan<byte> payload, out Rectangle area)
    {
        area = default;
        int offset;
        switch (message) {
            case MessageID.TileManipulation:
            case MessageID.ToggleDoorState:
            case MessageID.ChestUpdates:
            case MessageID.LockAndUnlock:
                offset = 1;
                break;
            case MessageID.SyncPlayerChest:
            case MessageID.ReadSign:
            case MessageID.ChestName:
                offset = 2;
                break;
            case MessageID.RequestChestOpen:
            case MessageID.RequestReadSign:
            case MessageID.TileSquare:
            case MessageID.HitSwitch:
            case MessageID.PaintTile:
            case MessageID.PaintWall:
            case MessageID.GemLockToggle:
                offset = 0;
                break;
            case MessageID.TemporaryAnimation:
                offset = 4;
                break;
            default:
                return false;
        }
        if (payload.Length < offset + 4 || message == MessageID.TileSquare && payload.Length < 7)
            throw new InvalidDataException("Incomplete tile update packet.");
        int x = BinaryPrimitives.ReadInt16LittleEndian(payload[offset..]);
        int y = BinaryPrimitives.ReadInt16LittleEndian(payload[(offset + 2)..]);
        area = new Rectangle(x, y, message == MessageID.TileSquare ? payload[4] : 1,
            message == MessageID.TileSquare ? payload[5] : 1);
        return true;
    }

    private static bool InvalidObjectRequest(int message, ReadOnlySpan<byte> payload, Rectangle area)
    {
        if (message == MessageID.ReadSign) {
            int index = BinaryPrimitives.ReadInt16LittleEndian(payload);
            Sign sign = (uint)index < Main.sign.Length ? Main.sign[index] : null;
            return (area.X >= Main.maxTilesX || sign?.x >= Main.maxTilesX)
                && (sign == null || sign.x != area.X || sign.y != area.Y);
        }
        if (message is MessageID.SyncPlayerChest or MessageID.ChestName) {
            int index = BinaryPrimitives.ReadInt16LittleEndian(payload);
            Chest chest = (uint)index < Main.chest.Length ? Main.chest[index] : null;
            return (area.X >= Main.maxTilesX || chest?.x >= Main.maxTilesX)
                && (chest == null || chest.x != area.X || chest.y != area.Y);
        }
        return false;
    }

    private static void SendUnpublishedTiles(List<Point> cells)
    {
        cells.Sort((left, right) => left.Y != right.Y ? left.Y.CompareTo(right.Y) : left.X.CompareTo(right.X));
        for (int i = 0; i < cells.Count;) {
            Point start = cells[i++];
            int width = 1;
            while (i < cells.Count && width < 32 && cells[i].Y == start.Y && cells[i].X == start.X + width) {
                width++;
                i++;
            }
            // SendData avoids SendTileSquare's additional RangeFrame mutation.
            NetMessage.SendData(MessageID.TileSquare, number: start.X, number2: start.Y,
                number3: width, number4: 1);
        }
    }
}
