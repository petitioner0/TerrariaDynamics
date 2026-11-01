using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ReLogic.Content;
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
    private Asset<Texture2D> activeTexture;

    public override void Load()
    {
        if (!Main.dedServ)
            activeTexture = ModContent.Request<Texture2D>(Texture + "_Active");
    }

    public override void Unload() => activeTexture = null;

    internal Texture2D ActiveTexture(int i, int j) =>
        IsActive(i, j) && !Main.tile[i, j].IsActuated && HasSupport(i, j)
            ? activeTexture.Value : null;

    // A full solid tile has unit mass. One thruster can balance ten such tiles.
    public const float Thrust = 10f * PhysicsSystem.Gravity;

    // Exhaust frames run clockwise: up, right, down, left. The artwork touches
    // its anchor; the arrow points outward with the exhaust, opposite to thrust.
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
        // Non-solid furniture needs this opt-in to receive normal hammer hits.
        TileID.Sets.CanBeSloped[Type] = true;
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
        Vector2 exhaust = ExhaustDirection(Main.tile[i, j]);
        YardCoordinates coordinates = yard.Coordinates;
        Vector2 localPoint = coordinates.StorageToLocalPixels(i, j) + new Vector2(8) - exhaust * 4;
        Vector2 force = coordinates.LocalVectorToWorld(-exhaust) * Thrust;
        yard.ApplyForceAtWorldPosition(force, coordinates.LocalToWorld(localPoint));
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

    private static bool HasSupport(int i, int j) => HasSupport(i, j, Direction(Main.tile[i, j]));

    private static bool HasSupport(int i, int j, int direction)
    {
        Vector2 exhaust = ExhaustDirection(direction);
        int x = i - (int)exhaust.X, y = j - (int)exhaust.Y;
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

    public override bool Slope(int i, int j)
    {
        if (YardSystem.Instance.TryStorageYard(i, j, out _))
            return false;

        Tile tile = Main.tile[i, j];
        int direction = Direction(tile);
        // Search clockwise without changing frames until a valid anchor is found.
        // If only the current face is supported, keep the existing direction.
        for (int offset = 1; offset < 4; offset++) {
            int nextDirection = (direction + offset) % 4;
            if (!HasSupport(i, j, nextDirection))
                continue;
            tile.TileFrameX = (short)(nextDirection * 18);
            if (Main.netMode != NetmodeID.SinglePlayer)
                NetMessage.SendTileSquare(-1, i, j, 1);
            break;
        }
        // Rotation replaces the normal slope/half-block action.
        return false;
    }

    private static Vector2 ExhaustDirection(Tile tile) => ExhaustDirection(Direction(tile));

    private static Vector2 ExhaustDirection(int direction) => direction switch {
        1 => Vector2.UnitX,
        2 => Vector2.UnitY,
        3 => -Vector2.UnitX,
        _ => -Vector2.UnitY
    };
}
