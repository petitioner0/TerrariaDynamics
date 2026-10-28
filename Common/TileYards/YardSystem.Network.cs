using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;
using Terraria.ModLoader.IO;
using TerrariaDynamics.Common.Lighting;
using TerrariaDynamics.Common.Networking;
using TerrariaDynamics.Common.Persistence;
using TerrariaDynamics.Common.Players;
using TerrariaDynamics.Common.Rendering;
using TerrariaDynamics.Common.Storage;
using TerrariaDynamics.Content.Items;

namespace TerrariaDynamics.Common.TileYards;

public sealed partial class YardSystem
{
    private readonly YardPacketAssembly incomingEvent = new();
    private readonly int[] lastRequests = new int[256];
    private readonly ulong[] snapshotTimes = new ulong[256];
    private readonly Dictionary<Guid, ulong> physicsPoseSequences = new();
    private int physicsSendTicks;
    private ulong nextPhysicsSequence;
    private long networkRevision;
    private int nextTransfer;
    private int nextRequest;
    private int pendingRequest;
    private Blueprint pendingBlueprint;
    private ulong pendingSince;
    private ulong snapshotSince;
    private ulong incomingSince;
    private bool snapshotRequested;
    private bool hasEnteredWorld;
    public bool IsSynchronized { get; private set; }
    public bool HasPendingRequest => pendingRequest != 0;

    // Only a revision fits here. Large persisted data uses the chunk protocol.
    public override void NetSend(BinaryWriter writer) => writer.Write(networkRevision);

    public override void NetReceive(BinaryReader reader)
    {
        long revision = reader.ReadInt64();
        if (hasEnteredWorld && IsSynchronized && revision > networkRevision)
            RequestSnapshot();
    }

    private void ClearNetwork()
    {
        incomingEvent.Clear();
        Array.Clear(lastRequests);
        Array.Clear(snapshotTimes);
        Array.Clear(testForceInputs);
        circuitActivations.Clear();
        physicsPoseSequences.Clear();
        ClearInteractions();
        physicsSendTicks = 0;
        nextPhysicsSequence = 0;
        networkRevision = 0;
        nextTransfer = nextRequest = pendingRequest = 0;
        pendingBlueprint = null;
        snapshotRequested = false;
        hasEnteredWorld = false;
        IsSynchronized = Main.netMode != NetmodeID.MultiplayerClient;
    }

    internal void EnterNetworkWorld()
    {
        hasEnteredWorld = true;
        RequestSnapshot();
    }

    private ModPacket Packet(YardPacketKind kind)
    {
        ModPacket packet = Mod.GetPacket();
        packet.Write(YardPacketCodec.ProtocolVersion);
        packet.Write((byte)kind);
        return packet;
    }

    internal void RequestSnapshot()
    {
        if (Main.netMode != NetmodeID.MultiplayerClient || snapshotRequested) return;
        IsSynchronized = false;
        snapshotRequested = true;
        snapshotSince = Main.GameUpdateCount;
        incomingEvent.Clear();
        Packet(YardPacketKind.RequestSnapshot).Send();
    }

    internal void RequestCreate(Blueprint blueprint, Player player, Rectangle source) =>
        SendRequest(blueprint, player, YardPacketKind.RequestCreate, source);

    internal void RequestReturn(Blueprint blueprint, Player player) =>
        SendRequest(blueprint, player, YardPacketKind.RequestReturn, default);

    private void SendRequest(Blueprint blueprint, Player player, YardPacketKind kind, Rectangle source)
    {
        if (!IsSynchronized || HasPendingRequest) return;
        pendingRequest = ++nextRequest;
        pendingBlueprint = blueprint;
        pendingSince = Main.GameUpdateCount;
        ModPacket packet = Packet(kind);
        packet.Write(pendingRequest);
        packet.Write((byte)player.selectedItem);
        packet.Write(blueprint.YardId.ToByteArray());
        if (kind == YardPacketKind.RequestCreate) {
            packet.Write(source.X); packet.Write(source.Y);
            packet.Write(source.Width); packet.Write(source.Height);
        }
        packet.Send();
        Main.NewText(Text("RequestSent"), Color.LightSkyBlue);
    }

    private void UpdateNetwork()
    {
        if (Main.netMode == NetmodeID.Server) {
            for (int i = 0; i < Main.maxPlayers; i++) {
                if (Main.player[i].active) continue;
                lastRequests[i] = 0;
                snapshotTimes[i] = 0;
                testForceInputs[i] = default;
                lastInteractions[i] = 0;
            }
            return;
        }
        if (Main.netMode != NetmodeID.MultiplayerClient) return;
        if (snapshotRequested && Main.GameUpdateCount - snapshotSince > 600) {
            snapshotRequested = false;
            RequestSnapshot();
        }
        if (!snapshotRequested && incomingEvent.IsReceiving && Main.GameUpdateCount - incomingSince > 600)
            RequestSnapshot();
        if (HasPendingRequest && Main.GameUpdateCount - pendingSince > 600) {
            pendingRequest = 0;
            pendingBlueprint = null;
            Main.NewText(Text("RequestTimedOut"), Color.Orange);
            RequestSnapshot();
        }
    }

    internal void HandlePacket(BinaryReader reader, int sender)
    {
        try {
            if (reader.ReadByte() != YardPacketCodec.ProtocolVersion)
                throw new InvalidDataException("Unsupported Yard network protocol.");
            var kind = (YardPacketKind)reader.ReadByte();
            if (Main.netMode == NetmodeID.Server) {
                if ((uint)sender >= Main.maxPlayers || !Main.player[sender].active) return;
                if (kind == YardPacketKind.RequestSnapshot) {
                    // A world contains large storage snapshots; avoid repeated
                    // expensive full encodes from the same client in one tick.
                    ulong now = Main.GameUpdateCount + 1;
                    if (snapshotTimes[sender] != 0 && now - snapshotTimes[sender] < 60) return;
                    snapshotTimes[sender] = now;
                    lock (worldSaveLock) SendSnapshot(sender);
                }
                else if (kind is YardPacketKind.RequestCreate or YardPacketKind.RequestReturn)
                    HandleRequest(reader, sender, kind);
                else if (kind == YardPacketKind.RequestForceInput)
                    ReceiveTestForceInput(reader, sender);
                else if (kind == YardPacketKind.PlayerAttachment)
                    ReceivePlayerAttachment(reader, sender);
                else if (kind == YardPacketKind.RequestTileInteraction)
                    ReceiveTileInteraction(reader, sender);
                else if (kind == YardPacketKind.RequestStopRest)
                    ReceiveStopRest(reader, sender);
                else if (kind == YardPacketKind.CircuitKeyInput)
                    ReceiveCircuitKeyInput(reader, sender);
                // Clients cannot submit event chunks or operation results.
            }
            else if (Main.netMode == NetmodeID.MultiplayerClient) {
                if (kind == YardPacketKind.EventChunk) {
                    if (!incomingEvent.IsReceiving) incomingSince = Main.GameUpdateCount;
                    byte[] bytes = incomingEvent.Read(reader);
                    if (bytes != null)
                        ReceiveEvent(incomingEvent.Kind, incomingEvent.Revision, YardPacketCodec.Decode(bytes));
                }
                else if (kind == YardPacketKind.Result)
                    ReceiveResult(reader);
                else if (kind == YardPacketKind.PhysicsTransforms)
                    ReceivePhysicsTransforms(reader);
                else if (kind == YardPacketKind.PlayerAttachment)
                    ReceivePlayerAttachment(reader, sender);
                else if (kind == YardPacketKind.PlayerRest)
                    ReceivePlayerRest(reader);
            }
        }
        catch (Exception error) {
            Mod.Logger.Warn($"Rejected Yard packet from {sender}.", error);
            if (Main.netMode == NetmodeID.MultiplayerClient) {
                incomingEvent.Clear();
                snapshotRequested = false;
                RequestSnapshot();
            }
        }
    }

    private void HandleRequest(BinaryReader reader, int sender, YardPacketKind kind)
    {
        int request = reader.ReadInt32();
        int slot = reader.ReadByte();
        Guid expectedId = YardPacketCodec.ReadId(reader);
        Rectangle source = kind == YardPacketKind.RequestCreate
            ? new Rectangle(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32()) : default;
        if (request <= 0 || request <= lastRequests[sender]) return;
        lastRequests[sender] = request;
        Player player = Main.player[sender];
        if (player.dead || slot != player.selectedItem || slot >= player.inventory.Length
            || player.inventory[slot].ModItem is not Blueprint blueprint || blueprint.YardId != expectedId) {
            SendResult(sender, request, false, true, Guid.Empty, Text("InvalidRequest"));
            return;
        }
        if (kind == YardPacketKind.RequestCreate) {
            if (expectedId != Guid.Empty) {
                SendResult(sender, request, false, true, Guid.Empty, Text("InvalidRequest"));
                return;
            }
            bool success = TryCreate(source, out Yard yard, out string message);
            if (success) {
                blueprint.Bind(yard);
                SyncBlueprint(player, blueprint);
            }
            SendResult(sender, request, success, true, yard?.Id ?? Guid.Empty, message);
            return;
        }
        Yard region = blueprint.BoundYard;
        if (region == null || expectedId == Guid.Empty) {
            SendResult(sender, request, false, true, Guid.Empty, Text("MissingYard", expectedId));
            return;
        }
        bool started = region.TryBeginReturn((success, message) => {
            if (!success) {
                PublishReturnState(region);
                Point origin = region.Coordinates.NearestWorldAnchorTile;
                BroadcastWorldArea(new Rectangle(origin.X, origin.Y, region.Width, region.Height));
            }
            SyncBlueprint(player, blueprint);
            SendResult(sender, request, success, true, Guid.Empty, message);
        }, out string returnMessage);
        if (started) PublishReturnState(region);
        SendResult(sender, request, started, !started, region.Id, returnMessage);
    }

    private static void SyncBlueprint(Player player, Blueprint blueprint)
    {
        // The requester may move the item while the return stability timer runs.
        for (int slot = 0; slot < player.inventory.Length; slot++) {
            if (ReferenceEquals(player.inventory[slot].ModItem, blueprint))
                NetMessage.SendData(MessageID.SyncEquipment, -1, -1, null, player.whoAmI, slot);
        }
    }

    private static void ClearReturnedBlueprints(Guid id)
    {
        void Clear(IEnumerable<Item> items)
        {
            if (items == null) return;
            foreach (Item item in items)
                (item?.ModItem as Blueprint)?.ClearBinding(id);
        }
        foreach (Player player in Main.player) {
            if (player == null) continue;
            Clear(player.inventory);
            Clear(player.bank?.item);
            Clear(player.bank2?.item);
            Clear(player.bank3?.item);
            Clear(player.bank4?.item);
        }
        foreach (Chest chest in Main.chest) Clear(chest?.item);
        Clear(Main.item);
    }

    private void SendResult(int client, int request, bool success, bool complete, Guid id, string message)
    {
        ModPacket packet = Packet(YardPacketKind.Result);
        packet.Write(request); packet.Write(success); packet.Write(complete);
        packet.Write(id.ToByteArray()); packet.Write(message);
        packet.Send(client);
    }

    private void ReceiveResult(BinaryReader reader)
    {
        int request = reader.ReadInt32();
        bool success = reader.ReadBoolean();
        bool complete = reader.ReadBoolean();
        Guid id = YardPacketCodec.ReadId(reader);
        string message = reader.ReadString();
        if (request != pendingRequest) return;
        if (success && id != Guid.Empty && TryGet(id, out Yard region)) {
            pendingBlueprint.Bind(region);
            Main.LocalPlayer.GetModPlayer<BlueprintSelectionPlayer>().Cancel();
        }
        Main.NewText(message, success ? Color.LightGreen : Color.Orange);
        if (complete) {
            pendingRequest = 0;
            pendingBlueprint = null;
        }
    }

    private TagCompound Snapshot(IEnumerable<Yard> yards)
    {
        var tag = new TagCompound();
        if (regions.Count > 0) {
            PrepareInteractionAirTile();
            Point air = YardStorageAllocator.ReservedTile.Value;
            tag["airX"] = air.X;
            tag["airY"] = air.Y;
        }
        YardWorldData.Save(tag, yards, yard => yard.IsLoaded ? YardTileCodec.Save(Main.tile, yard) : yard.PendingTiles);
        foreach (TagCompound saved in tag.GetList<TagCompound>("yards"))
            saved["returning"] = regions[Guid.Parse(saved.GetString("id"))].State == YardState.Returning;
        return tag;
    }

    private void SendSnapshot(int client)
    {
        TagCompound tag = Snapshot(regions.Values);
        YardNetworkObjects.Save(tag, regions.Values);
        SendEvent(YardEventKind.Snapshot, tag, client);
        foreach (Player player in Main.player)
            if (player != null && player.active)
                SendPlayerRest(player.GetModPlayer<YardRestingPlayer>().State, client);
    }

    private void SendEvent(YardEventKind kind, TagCompound tag, int client = -1)
    {
        byte[] bytes = YardPacketCodec.Encode(tag);
        int transfer = ++nextTransfer;
        for (int offset = 0; offset < bytes.Length; offset += YardPacketCodec.ChunkSize) {
            int count = Math.Min(YardPacketCodec.ChunkSize, bytes.Length - offset);
            ModPacket packet = Packet(YardPacketKind.EventChunk);
            packet.Write(transfer); packet.Write((byte)kind); packet.Write(networkRevision);
            packet.Write(bytes.Length); packet.Write(offset); packet.Write((ushort)count);
            packet.Write(bytes, offset, count);
            packet.Send(client);
        }
    }

    private void PublishCreated(Yard yard) => Publish(() => {
        TagCompound tag = Snapshot(new[] { yard });
        YardNetworkObjects.Save(tag, regions.Values);
        try { SendEvent(YardEventKind.Created, tag); }
        finally { BroadcastWorldArea(yard.Source); }
    });

    private void PublishReturned(Guid id, Rectangle bounds) => Publish(() => {
        var tag = new TagCompound { ["id"] = id.ToString("D"), ["tileX"] = bounds.X, ["tileY"] = bounds.Y };
        YardNetworkObjects.Save(tag, regions.Values, bounds);
        try { SendEvent(YardEventKind.Returned, tag); }
        finally { BroadcastWorldArea(bounds); }
    });

    private void PublishReturnState(Yard yard) => Publish(() => {
        var tag = new TagCompound {
            ["id"] = yard.Id.ToString("D"), ["worldX"] = yard.WorldPosition.X,
            ["worldY"] = yard.WorldPosition.Y, ["rotation"] = yard.Rotation,
            ["returning"] = yard.State == YardState.Returning
        };
        YardNetworkObjects.Save(tag, regions.Values);
        SendEvent(YardEventKind.ReturnState, tag);
    });

    // Motion has its own sequence and does not invalidate lifecycle snapshots or
    // resend tile/object data. Clients never run an independent physics world.
    private void PublishPhysicsTransforms()
    {
        if (Main.netMode != NetmodeID.Server)
            return;
        if (regions.Count == 0) {
            physicsSendTicks = 0;
            return;
        }
        if (++physicsSendTicks < YardPhysicsPacket.SendIntervalTicks)
            return;
        physicsSendTicks = 0;
        ulong sequence = ++nextPhysicsSequence;
        var poses = new List<YardPhysicsPose>(Math.Min(regions.Count, YardPhysicsPacket.MaxPoses));
        try {
            foreach (Yard yard in regions.Values) {
                if (!yard.IsLoaded)
                    continue;
                poses.Add(new YardPhysicsPose(yard.Id, yard.WorldPosition, yard.Rotation,
                    yard.LinearVelocity, yard.AngularVelocity));
                if (poses.Count == YardPhysicsPacket.MaxPoses)
                    SendBatch();
            }
            if (poses.Count > 0)
                SendBatch();
        }
        catch (Exception error) {
            Mod.Logger.Warn("Yard physics poses could not be broadcast.", error);
        }

        void SendBatch()
        {
            ModPacket packet = Packet(YardPacketKind.PhysicsTransforms);
            YardPhysicsPacket.Write(packet, networkRevision, sequence, poses.ToArray());
            packet.Send();
            poses.Clear();
        }
    }

    private void ReceivePhysicsTransforms(BinaryReader reader)
    {
        YardPhysicsPose[] poses = YardPhysicsPacket.Read(reader, out long revision, out ulong sequence);
        if (!IsSynchronized || revision < networkRevision)
            return;
        if (revision > networkRevision) {
            RequestSnapshot();
            return;
        }
        double time = YardRenderClock.Seconds;
        foreach (YardPhysicsPose pose in poses) {
            if (!TryGet(pose.Id, out Yard yard)) {
                RequestSnapshot();
                return;
            }
            if (!yard.IsLoaded || (physicsPoseSequences.TryGetValue(pose.Id, out ulong previous) && sequence <= previous))
                continue;
            yard.ApplyRemotePhysicsState(pose.Position, pose.Rotation, pose.Velocity, pose.AngularVelocity, sequence, time);
            physicsPoseSequences[pose.Id] = sequence;
        }
    }

    private void Publish(Action send)
    {
        if (Main.netMode != NetmodeID.Server) return;
        networkRevision++;
        try { send(); }
        catch (Exception error) {
            // Networking must never turn a committed transfer into a failed
            // operation or make a blueprint lose ownership of its Yard.
            Mod.Logger.Error("Yard committed, but event broadcast failed.", error);
        }
    }

    private static void BroadcastWorldArea(Rectangle area)
    {
        if (Main.netMode != NetmodeID.Server) return;
        area.Inflate(2, 2);
        area = Rectangle.Intersect(area, PlayableBounds);
        // Each cell can carry frames/coatings/mod IDs; keep vanilla packets small.
        for (int x = area.Left; x < area.Right; x += 32)
            for (int y = area.Top; y < area.Bottom; y += 32)
                NetMessage.SendTileSquare(-1, x, y, Math.Min(32, area.Right - x), Math.Min(32, area.Bottom - y));
        // Support checks can remove a whole tree/vine or multi-tile outside this
        // ring. Let vanilla resend visible sections and lazily reload distant
        // sections so those recursive changes cannot remain stale on clients.
        Netplay.ResetSections();
    }

    private void ReceiveEvent(YardEventKind kind, long revision, TagCompound tag)
    {
        if (kind != YardEventKind.Snapshot) {
            if (!IsSynchronized || revision <= networkRevision) return;
            if (revision != networkRevision + 1) {
                RequestSnapshot();
                return;
            }
        }
        else if (revision < networkRevision) return;

        List<Yard> loaded = kind is YardEventKind.Snapshot or YardEventKind.Created
            ? YardWorldData.Load(tag, Main.maxTilesX, Main.maxTilesY, Main.tile.Height) : null;
        if (kind == YardEventKind.Created && (loaded.Count != 1 || regions.ContainsKey(loaded[0].Id)
            || regions.Values.Any(yard => yard.Bounds.Intersects(loaded[0].Bounds))))
            throw new InvalidDataException("Invalid Yard creation event.");
        int width = Math.Max(Main.tile.Width, loaded?.Select(yard => yard.Bounds.Right).DefaultIfEmpty(0).Max() ?? 0);
        Point? airCell = null;
        if (loaded != null && loaded.Count > 0) {
            if (!tag.ContainsKey("airX") || !tag.ContainsKey("airY"))
                throw new InvalidDataException("Missing authoritative Yard neighbor-read address.");
            Point air = new(tag.GetInt("airX"), tag.GetInt("airY"));
            if (air.X < Math.Max(Main.maxTilesX, YardStorageAllocator.StartX)
                || air.X >= YardStorageAllocator.CoordinateLimit || air.Y < 0 || air.Y >= Main.tile.Height
                || loaded.Any(yard => yard.Bounds.Contains(air))
                || kind != YardEventKind.Snapshot && regions.Values.Any(yard => yard.Bounds.Contains(air)))
                throw new InvalidDataException("Invalid Yard neighbor-read address.");
            airCell = air;
            width = Math.Max(width, air.X + 1);
        }
        YardNetworkObjects objects = YardNetworkObjects.Load(tag, width, Main.tile.Height);
        if (kind == YardEventKind.Snapshot) {
            physicsPoseSequences.Clear();
            HashSet<Guid> ids = loaded.Select(yard => yard.Id).ToHashSet();
            foreach (Yard old in regions.Values) {
                ClearStorage(old);
                if (!ids.Contains(old.Id)) ClearReturnedBlueprints(old.Id);
                old.Invalidate(removed: !ids.Contains(old.Id));
            }
            regions.Clear();
            YardLighting.Clear();
            WorldRevision++;
        }
        if (loaded != null) {
            EnsureStorage(width);
            YardStorageAllocator.ReservedTile = airCell;
            if (airCell is Point air)
                Main.tile[air.X, air.Y].ClearEverything();
            foreach (Yard yard in loaded) {
                ClearStorage(yard);
                yard.Attach(this);
                regions.Add(yard.Id, yard);
                try {
                    int count = YardTileCodec.Load(Main.tile, yard, yard.PendingTiles);
                    YardLighting.CaptureContour(yard, fromSource: false);
                    yard.MarkLoaded(count);
                    TagCompound saved = tag.GetList<TagCompound>("yards").First(entry => entry.GetString("id") == yard.Id.ToString("D"));
                    yard.ApplyRemoteState(yard.WorldPosition, yard.Rotation, saved.GetBool("returning"));
                    if (kind == YardEventKind.Created)
                        MoveLocalCircuitBindings(Guid.Empty, yard.Source, yard.Id, Point.Zero);
                }
                catch (Exception error) {
                    Mod.Logger.Error($"Network Yard {yard.Id} could not be loaded. Payload preserved.", error);
                }
            }
            WorldRevision++;
        }
        else {
            if (!Guid.TryParse(tag.GetString("id"), out Guid id) || !TryGet(id, out Yard yard))
                throw new InvalidDataException("Yard event refers to an unknown ID.");
            if (kind == YardEventKind.Returned) {
                Point origin = new(tag.GetInt("tileX"), tag.GetInt("tileY"));
                if (!tag.ContainsKey("tileX") || !tag.ContainsKey("tileY")
                    || !PlayableBounds.Contains(new Rectangle(origin.X, origin.Y, yard.Width, yard.Height)))
                    throw new InvalidDataException("Invalid Yard return position.");
                MoveLocalCircuitBindings(yard.Id, new Rectangle(0, 0, yard.Width, yard.Height), Guid.Empty, origin);
                physicsPoseSequences.Remove(id);
                ClearStorage(yard);
                regions.Remove(id);
                YardLighting.Remove(id);
                ClearReturnedBlueprints(id);
                yard.Invalidate(removed: true);
                WorldRevision++;
            }
            else
                yard.ApplyRemoteState(new Vector2(tag.GetFloat("worldX"), tag.GetFloat("worldY")),
                    tag.GetFloat("rotation"), tag.GetBool("returning"));
        }
        objects.Apply();
        networkRevision = revision;
        if (kind == YardEventKind.Snapshot) {
            IsSynchronized = true;
            snapshotRequested = false;
        }
        Main.refreshMap = true;
        Main.renderCount = 99;
    }

    private static void ClearStorage(Yard yard)
    {
        if (yard.Bounds.Right > Main.tile.Width) return;
        for (int x = yard.Bounds.Left; x < yard.Bounds.Right; x++)
            for (int y = yard.Bounds.Top; y < yard.Bounds.Bottom; y++)
                Main.tile[x, y].ClearEverything();
    }
}
