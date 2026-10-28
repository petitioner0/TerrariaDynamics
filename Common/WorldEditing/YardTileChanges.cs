using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using Terraria.ObjectData;
using TerrariaDynamics.Common.Physics;

namespace TerrariaDynamics.Common.WorldEditing;

// Capture only cells visited by the scoped native helpers. Tile is a live handle,
// so retaining the handle itself would lose the state before a field write.
internal sealed class YardTileChanges
{
    private readonly Dictionary<Point, CellState> before = new();
    private readonly Dictionary<Point, CellState> sent = new();
    private readonly HashSet<Point> commands = new();

    public void Observe(int x, int y, Tile tile)
    {
        Point cell = new(x, y);
        if (!before.ContainsKey(cell))
            before.Add(cell, CellState.Capture(tile));
    }

    public void Observe(Rectangle area)
    {
        for (int x = area.Left; x < area.Right; x++)
            for (int y = area.Top; y < area.Bottom; y++)
                Observe(x, y, Main.tile[x, y]);
    }

    public void Sent(Rectangle area)
    {
        for (int x = area.Left; x < area.Right; x++)
            for (int y = area.Top; y < area.Bottom; y++) {
                sent[new Point(x, y)] = CellState.Capture(Main.tile[x, y]);
                commands.Remove(new Point(x, y));
            }
    }

    public void Command(Rectangle area)
    {
        Observe(area);
        for (int x = area.Left; x < area.Right; x++)
            for (int y = area.Top; y < area.Bottom; y++)
                commands.Add(new Point(x, y));
    }

    public List<Point> Complete(bool receiving, out int contentDelta, out bool collisionChanged,
        out bool changed)
    {
        contentDelta = 0;
        collisionChanged = changed = false;
        var unsent = new List<Point>();
        var removedObjects = new HashSet<(Point Anchor, bool Chest)>();
        foreach (var pair in before) {
            Point cell = pair.Key;
            CellState old = pair.Value, current = CellState.Capture(Main.tile[cell.X, cell.Y]);
            // Also correct a published intermediate state that reverted to the
            // original value, and operation packets following a state packet.
            if (!receiving && (commands.Contains(cell)
                || (sent.TryGetValue(cell, out CellState published) ? published != current : old != current)))
                unsent.Add(cell);
            if (old == current)
                continue;
            changed = true;
            contentDelta += (current.HasContent ? 1 : 0) - (old.HasContent ? 1 : 0);
            collisionChanged |= old.Shape != current.Shape;
            if (receiving && old.HasTile && (!current.HasTile || old.Type != current.Type)
                && (TileID.Sets.BasicChest[old.Type] || TileID.Sets.BasicDresser[old.Type]
                    || old.Type is TileID.Signs or TileID.Tombstones or TileID.AnnouncementBox)) {
                TileObjectData data = TileObjectData.GetTileData(old.Type, 0);
                int width = data?.Width ?? 2, height = data?.Height ?? 2;
                Point anchor = new(cell.X - old.FrameX / 18 % width, cell.Y - old.FrameY / 18 % height);
                removedObjects.Add((anchor, TileID.Sets.BasicChest[old.Type] || TileID.Sets.BasicDresser[old.Type]));
            }
        }
        // A state packet clears tiles directly. Reconcile the local directory for
        // objects removed by a support check without a separate operation packet.
        foreach (var entry in removedObjects) {
            Tile remaining = Main.tile[entry.Anchor.X, entry.Anchor.Y];
            if (entry.Chest) {
                if (remaining.HasTile && (TileID.Sets.BasicChest[remaining.TileType]
                    || TileID.Sets.BasicDresser[remaining.TileType]))
                    continue;
                int index = Chest.FindChest(entry.Anchor.X, entry.Anchor.Y);
                if (index >= 0) {
                    foreach (Player player in Main.player)
                        if (player != null && player.chest == index)
                            player.chest = -1;
                    Chest.DestroyChestDirect(entry.Anchor.X, entry.Anchor.Y, index);
                }
            }
            else if (!remaining.HasTile || remaining.TileType is not (TileID.Signs or TileID.Tombstones or TileID.AnnouncementBox)) {
                foreach (Player player in Main.player) {
                    if (player == null || (uint)player.sign >= Main.sign.Length)
                        continue;
                    Sign sign = Main.sign[player.sign];
                    if (sign == null || sign.x != entry.Anchor.X || sign.y != entry.Anchor.Y)
                        continue;
                    player.sign = -1;
                    if (player.whoAmI == Main.myPlayer)
                        Main.editSign = false;
                }
                Sign.KillSign(entry.Anchor.X, entry.Anchor.Y);
            }
        }
        return unsent;
    }

    private readonly record struct CellState(ushort Type, ushort Wall, short FrameX, short FrameY,
        int WallFrameX, int WallFrameY, byte TileColor, byte WallColor, byte FrameNumber,
        byte WallFrameNumber, byte Slope, byte Liquid, byte LiquidType, ushort Flags,
        bool HasContent, TerrainTileShape Shape)
    {
        public bool HasTile => (Flags & 2048) != 0;

        public static CellState Capture(Tile tile) => new(tile.TileType, tile.WallType,
            tile.TileFrameX, tile.TileFrameY, tile.WallFrameX, tile.WallFrameY,
            tile.TileColor, tile.WallColor, (byte)tile.TileFrameNumber, (byte)tile.WallFrameNumber,
            (byte)tile.Slope, tile.LiquidAmount, (byte)tile.LiquidType,
            (ushort)((tile.IsHalfBlock ? 1 : 0) | (tile.IsActuated ? 2 : 0)
                | (tile.HasActuator ? 4 : 0) | (tile.RedWire ? 8 : 0) | (tile.BlueWire ? 16 : 0)
                | (tile.GreenWire ? 32 : 0) | (tile.YellowWire ? 64 : 0)
                | (tile.IsTileInvisible ? 128 : 0) | (tile.IsWallInvisible ? 256 : 0)
                | (tile.IsTileFullbright ? 512 : 0) | (tile.IsWallFullbright ? 1024 : 0)
                | (tile.HasTile ? 2048 : 0)), YardTileTransfer.HasContent(tile), TileCollisionRules.ShapeOf(tile));
    }
}
