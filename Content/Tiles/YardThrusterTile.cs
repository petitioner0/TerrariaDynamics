using Microsoft.Xna.Framework;
using Terraria;
using Terraria.DataStructures;
using Terraria.Enums;
using Terraria.ID;
using Terraria.ModLoader;
using Terraria.ObjectData;
using TerrariaDynamics.Common.Circuitry;
using TerrariaDynamics.Common.Physics;
using TerrariaDynamics.Common.TileYards;
using TerrariaDynamics.Content.Items;

namespace TerrariaDynamics.Content.Tiles;

public sealed class YardThrusterTile : KeybindCircuitTile
{
    // A full solid tile has unit mass. One thruster can balance ten such tiles.
    public const float Thrust = 10f * PhysicsSystem.Gravity;

    // Frame order is clockwise: up, right, down, left. The half-cell artwork
    // touches its anchor; the arrow points away from that anchor.
    private static int Direction(Tile tile) => tile.TileFrameX / 18 % 4;

    public override bool SendsWireSignalOnKeyPress => false;

    // YardSystem still requires a live player deeply bound to this exact Yard.
    public override bool CanTriggerWithKey(Player player, int i, int j) => true;

    public override void SetStaticDefaults()
    {
        Main.tileFrameImportant[Type] = true;
        Main.tileNoAttach[Type] = true;
        Main.tileLavaDeath[Type] = false;
        // Non-solid furniture in both the main world and Yard physics.
        Main.tileSolid[Type] = false;
        Main.tileSolidTop[Type] = false;
        DustType = DustID.Iron;
        HitSound = SoundID.Dig;

        TileObjectData.newTile.CopyFrom(TileObjectData.Style1x1);
        TileObjectData.newTile.CoordinateHeights = new[] { 16 };
        TileObjectData.newTile.StyleHorizontal = true;
        TileObjectData.newTile.StyleMultiplier = 4;
        TileObjectData.newTile.LavaDeath = false;
        TileObjectData.newTile.AnchorBottom = new AnchorData(AnchorType.SolidTile, 1, 0);

        for (int direction = 1; direction < 4; direction++) {
            TileObjectData.newAlternate.CopyFrom(TileObjectData.newTile);
            TileObjectData.newAlternate.AnchorBottom = AnchorData.Empty;
            AnchorData anchor = new(AnchorType.SolidTile, 1, 0);
            if (direction == 1)
                TileObjectData.newAlternate.AnchorLeft = anchor;
            else if (direction == 2)
                TileObjectData.newAlternate.AnchorTop = anchor;
            else
                TileObjectData.newAlternate.AnchorRight = anchor;
            TileObjectData.addAlternate(direction);
        }
        TileObjectData.addTile(Type);
        RegisterItemDrop(ModContent.ItemType<YardThruster>());
        AddMapEntry(new Color(100, 180, 195), CreateMapEntryName());
    }

    protected override void OnCircuitTriggered(int i, int j)
    {
        // The rising edge only checks attachment. Force belongs to the active
        // state update, so holding a key does not repeat the edge callback.
        CheckSupport(i, j);
    }

    protected override void OnCircuitActive(int i, int j, Yard yard)
    {
        if (!HasSupport(i, j))
            return;
        Vector2 normal = Normal(Main.tile[i, j]);
        YardCoordinates coordinates = yard.Coordinates;
        Vector2 localPoint = coordinates.StorageToLocalPixels(i, j) + new Vector2(8) - normal * 4;
        yard.ApplyForceAtWorldPosition(coordinates.LocalVectorToWorld(normal) * Thrust,
            coordinates.LocalToWorld(localPoint));
    }

    public override bool TileFrame(int i, int j, ref bool resetFrame, ref bool noBreak)
    {
        if (!noBreak && !WorldGen.noTileActions)
            CheckSupport(i, j);
        return false;
    }

    private void CheckSupport(int i, int j)
    {
        if (Main.netMode == NetmodeID.MultiplayerClient || HasSupport(i, j))
            return;
        WorldGen.KillTile(i, j);
        if (Main.netMode == NetmodeID.Server)
            NetMessage.SendTileSquare(-1, i, j, 1);
    }

    private static bool HasSupport(int i, int j)
    {
        Vector2 normal = Normal(Main.tile[i, j]);
        int x = i - (int)normal.X, y = j - (int)normal.Y;
        if (YardSystem.Instance.TryStorageYard(i, j, out Yard yard)) {
            if (!yard.Bounds.Contains(x, y))
                return false;
        }
        else if (x < 0 || y < 0 || x >= Main.maxTilesX || y >= Main.maxTilesY)
            return false;
        Tile support = Main.tile[x, y];
        return support.HasTile && !support.IsActuated && !support.IsHalfBlock
            && support.Slope == SlopeType.Solid && Main.tileSolid[support.TileType]
            && !Main.tileSolidTop[support.TileType] && !Main.tileNoAttach[support.TileType];
    }

    public override bool CanDrop(int i, int j)
    {
        if (!YardSystem.Instance.TryStorageYard(i, j, out Yard yard))
            return true;
        // Storage addresses are outside the playable map. Spawn at the actual
        // Yard position instead, and suppress the normal storage-space drop.
        if (Main.netMode != NetmodeID.MultiplayerClient) {
            Vector2 position = yard.Coordinates.LocalToWorld(
                yard.Coordinates.StorageToLocalPixels(i, j) + new Vector2(8));
            Item.NewItem(WorldGen.GetItemSource_FromTileBreak(i, j),
                (int)position.X - 8, (int)position.Y - 8, 16, 16, ModContent.ItemType<YardThruster>());
        }
        return false;
    }

    // Hammering must not turn the directional furniture into a slope.
    public override bool Slope(int i, int j) => false;

    private static Vector2 Normal(Tile tile) => Direction(tile) switch {
        1 => Vector2.UnitX,
        2 => Vector2.UnitY,
        3 => -Vector2.UnitX,
        _ => -Vector2.UnitY
    };
}
