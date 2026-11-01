using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ReLogic.Content;
using Terraria;
using Terraria.DataStructures;
using Terraria.ID;
using Terraria.ModLoader;
using Terraria.ObjectData;
using TerrariaDynamics.Common.Circuitry;
using TerrariaDynamics.Common.TileYards;
using TerrariaDynamics.Content.Items;

namespace TerrariaDynamics.Content.Tiles;

public sealed class YardStabilizerTile : KeybindCircuitTile
{
    // Inertia-scaled proportional torque, with critical damping for one device.
    // Gains use radians and seconds, independently of the Yard's size/mass.
    public const float RightingStrength = 16f;
    public const float AngularDamping = 8f;

    private Asset<Texture2D> activeTexture;

    private static int Direction(Tile tile) => tile.TileFrameX / 18 % 4;

    private static Vector2 LocalNormal(Tile tile) => Direction(tile) switch {
        1 => Vector2.UnitX,
        2 => Vector2.UnitY,
        3 => -Vector2.UnitX,
        _ => -Vector2.UnitY
    };

    public override void Load()
    {
        if (!Main.dedServ)
            activeTexture = ModContent.Request<Texture2D>(Texture + "_Active");
    }

    public override void Unload() => activeTexture = null;

    internal Texture2D ActiveTexture(int i, int j) =>
        IsActive(i, j) && !Main.tile[i, j].IsActuated ? activeTexture.Value : null;

    public override bool SendsWireSignalOnKeyPress => false;
    public override bool CanTriggerWithKey(Player player, int i, int j) => true;

    public override void SetStaticDefaults()
    {
        Main.tileFrameImportant[Type] = true;
        Main.tileSolid[Type] = true;
        Main.tileBlockLight[Type] = true;
        Main.tileLavaDeath[Type] = false;
        DustType = DustID.Iron;
        HitSound = SoundID.Dig;
        TileObjectData.newTile.CopyFrom(TileObjectData.Style1x1);
        TileObjectData.newTile.CoordinateHeights = new[] { 16 };
        TileObjectData.newTile.StyleHorizontal = true;
        TileObjectData.newTile.AnchorBottom = AnchorData.Empty;
        // Keep normal terrain-tile placement rather than furniture anchoring.
        TileObjectData.newTile.UsesCustomCanPlace = false;
        TileObjectData.newTile.LavaDeath = false;
        TileObjectData.addTile(Type);
        RegisterItemDrop(ModContent.ItemType<YardStabilizer>());
        AddMapEntry(new Color(195, 155, 80), CreateMapEntryName());
    }

    protected override void OnCircuitTriggered(int i, int j)
    {
        // Torque belongs to the continuous active update, not the rising edge.
    }

    protected override void OnCircuitActive(int i, int j, Yard yard)
    {
        // The stored arrow direction is the local normal. atan2 gives its signed tilt
        // from world up in [-pi, pi], including nonzero torque when inverted.
        Vector2 normal = yard.Coordinates.LocalVectorToWorld(LocalNormal(Main.tile[i, j]));
        float tilt = MathF.Atan2(normal.X, -normal.Y);
        float torque = yard.PhysicsRotationalInertia
            * (-RightingStrength * tilt - AngularDamping * yard.AngularVelocity);
        yard.ApplyTorque(torque);
    }

    public override bool TileFrame(int i, int j, ref bool resetFrame, ref bool noBreak)
    {
        // Preserve the selected direction when neighbouring blocks are framed.
        Main.tile[i, j].TileFrameY = 0;
        return false;
    }

    public override void SetDrawPositions(int i, int j, ref int width, ref int offsetY,
        ref int height, ref short tileFrameX, ref short tileFrameY)
    {
        tileFrameX = (short)(Direction(Main.tile[i, j]) * 18);
        tileFrameY = 0;
    }

    public override bool Slope(int i, int j)
    {
        // Called by normal hammer use on the local client/single player.
        // Yard storage must keep the direction selected before capture.
        if (YardSystem.Instance.TryStorageYard(i, j, out _))
            return false;

        Tile tile = Main.tile[i, j];
        tile.TileFrameX = (short)((Direction(tile) + 1) % 4 * 18);
        if (Main.netMode != NetmodeID.SinglePlayer)
            NetMessage.SendTileSquare(-1, i, j, 1);
        return false;
    }

    public override bool CanDrop(int i, int j)
    {
        if (!YardSystem.Instance.TryStorageYard(i, j, out Yard yard))
            return true;
        if (Main.netMode != NetmodeID.MultiplayerClient) {
            Vector2 position = yard.Coordinates.LocalToWorld(
                yard.Coordinates.StorageToLocalPixels(i, j) + new Vector2(8));
            Item.NewItem(WorldGen.GetItemSource_FromTileBreak(i, j),
                (int)position.X - 8, (int)position.Y - 8, 16, 16, ModContent.ItemType<YardStabilizer>());
        }
        return false;
    }
}
