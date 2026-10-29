using Terraria.ID;
using TerrariaDynamics.Common.Circuitry;
using TerrariaDynamics.Content.Tiles;

namespace TerrariaDynamics.Content.Items;

public sealed class YardAnchor : KeybindCircuitItem<YardAnchorTile>
{
    public override void SetDefaults()
    {
        base.SetDefaults();
        Item.width = 16;
        Item.height = 16;
        Item.rare = ItemRarityID.Blue;
    }

    public override void AddRecipes() => CreateRecipe().AddIngredient(ItemID.Wood).Register();
}
