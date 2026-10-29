using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ReLogic.Content;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;
using TerrariaDynamics.Common.Circuitry;
using TerrariaDynamics.Common.TileYards;
using TerrariaDynamics.Content.Items;

namespace TerrariaDynamics.Content.Tiles;

public sealed class YardAnchorTile : KeybindCircuitTile
{
    private Asset<Texture2D> activeTexture;

    public override void Load()
    {
        if (!Main.dedServ)
            activeTexture = ModContent.Request<Texture2D>(Texture + "_Active");
    }

    public override void Unload() => activeTexture = null;

    internal Texture2D ActiveTexture(int i, int j) =>
        IsActive(i, j) && !Main.tile[i, j].IsActuated ? activeTexture.Value : null;

    public override bool SendsWireSignalOnKeyPress => false;
    public override bool KeepsYardStaticWhileActive => true;
    public override bool CanTriggerWithKey(Player player, int i, int j) => true;

    public override void SetStaticDefaults()
    {
        Main.tileSolid[Type] = true;
        Main.tileBlockLight[Type] = true;
        Main.tileLavaDeath[Type] = false;
        DustType = DustID.Iron;
        HitSound = SoundID.Dig;
        RegisterItemDrop(ModContent.ItemType<YardAnchor>());
        AddMapEntry(new Color(140, 155, 170), CreateMapEntryName());
    }

    protected override void OnCircuitTriggered(int i, int j)
    {
        // Body type is resolved from every active anchor before physics forces.
    }

    public override bool TileFrame(int i, int j, ref bool resetFrame, ref bool noBreak)
    {
        // Each block keeps its own casing instead of merging with neighbours.
        Main.tile[i, j].TileFrameX = 0;
        Main.tile[i, j].TileFrameY = 0;
        return false;
    }

    public override void SetDrawPositions(int i, int j, ref int width, ref int offsetY,
        ref int height, ref short tileFrameX, ref short tileFrameY)
    {
        // Also draw already-placed blocks with the single-frame texture.
        tileFrameX = 0;
        tileFrameY = 0;
    }

    public override bool Slope(int i, int j) => false;

    public override bool CanDrop(int i, int j)
    {
        if (!YardSystem.Instance.TryStorageYard(i, j, out Yard yard))
            return true;
        if (Main.netMode != NetmodeID.MultiplayerClient) {
            Vector2 position = yard.Coordinates.LocalToWorld(
                yard.Coordinates.StorageToLocalPixels(i, j) + new Vector2(8));
            Item.NewItem(WorldGen.GetItemSource_FromTileBreak(i, j),
                (int)position.X - 8, (int)position.Y - 8, 16, 16, ModContent.ItemType<YardAnchor>());
        }
        return false;
    }
}
