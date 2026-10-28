using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace TerrariaDynamics.Content.Items;

public sealed class YardForceTester : ModItem
{
    public override string Texture => "Terraria/Images/Item_" + ItemID.Wrench;

    public override void SetStaticDefaults() => ItemID.Sets.ItemsThatAllowRepeatedRightClick[Type] = true;

    public override void SetDefaults()
    {
        Item.width = 24;
        Item.height = 28;
        Item.maxStack = 1;
        Item.useStyle = ItemUseStyleID.HoldUp;
        Item.useTime = 15;
        Item.useAnimation = 15;
        Item.noMelee = true;
        Item.autoReuse = true;
        Item.rare = ItemRarityID.Blue;
        Item.value = 0;
    }

    public override bool AltFunctionUse(Player player) => true;

    public override bool CanUseItem(Player player) => player.altFunctionUse == 2;

    public override bool? UseItem(Player player) => true;

    public override void AddRecipes() => CreateRecipe().AddIngredient(ItemID.Wood).Register();
}
