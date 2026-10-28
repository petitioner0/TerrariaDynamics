using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;
using Terraria.ModLoader.IO;
using TerrariaDynamics.Common.Networking;
using TerrariaDynamics.Common.Players;
using TerrariaDynamics.Common.TileYards;

namespace TerrariaDynamics.Content.Items;

public sealed class Blueprint : ModItem
{
    // Use a vanilla asset while the prototype has no custom artwork.
    public override string Texture => "Terraria/Images/Item_" + ItemID.Book;
    private YardBinding binding = new();
    public Guid YardId => binding.Id;
    public Yard BoundYard => binding.Resolve(YardSystem.Instance);

    internal void Bind(Yard yard) => binding.Bind(yard, YardSystem.Instance);

    internal void ClearBinding(Guid id)
    {
        if (binding.Id == id)
            binding = new YardBinding();
    }

    public override ModItem Clone(Item newEntity)
    {
        var clone = (Blueprint)base.Clone(newEntity);
        clone.binding = binding.Clone();
        return clone;
    }

    public override void SetDefaults()
    {
        Item.width = 24;
        Item.height = 28;
        Item.maxStack = 1;
        Item.useStyle = ItemUseStyleID.HoldUp;
        Item.useTime = 15;
        Item.useAnimation = 15;
        Item.noMelee = true;
        Item.autoReuse = false;
        Item.rare = ItemRarityID.Blue;
        Item.value = 0;
    }

    public override bool AltFunctionUse(Player player) => true;

    public override bool? UseItem(Player player)
    {
        if (player.whoAmI != Main.myPlayer)
            return false;
        YardSystem system = YardSystem.Instance;
        if (Main.netMode == NetmodeID.MultiplayerClient && (!system.IsSynchronized || system.HasPendingRequest)) {
            Main.NewText(YardSystem.Text(system.IsSynchronized ? "RequestPending" : "SyncWaiting"), Color.Orange);
            return false;
        }
        var selection = player.GetModPlayer<BlueprintSelectionPlayer>();
        Yard yard = BoundYard;
        if (YardId != Guid.Empty) {
            selection.Cancel();
            if (yard == null) {
                Main.NewText(YardSystem.Text("MissingYard", YardId), Color.Orange);
                return true;
            }
            if (player.altFunctionUse == 2) {
                if (player.mouseInterface || Main.mapFullscreen)
                    return false;
                if (Main.netMode == NetmodeID.MultiplayerClient) {
                    system.RequestReturn(this, player);
                    return true;
                }
                bool started = yard.TryBeginReturn((returned, message) => {
                    Main.NewText(message, returned ? Color.LightGreen : Color.Orange);
                }, out string returnMessage);
                Main.NewText(returnMessage, started ? Color.LightSkyBlue : Color.Orange);
                return true;
            }
            Main.NewText(YardSystem.Text("YardInfo", yard.Id, yard.Bounds.X,
                yard.Bounds.Y, yard.Width, yard.Height), Color.LightSkyBlue);
            return true;
        }
        if (player.altFunctionUse == 2) {
            selection.Cancel();
            Main.NewText(YardSystem.Text("SelectionCancelled"), Color.LightSkyBlue);
            return true;
        }
        if (player.mouseInterface || Main.mapFullscreen)
            return false;

        Point corner = Main.MouseWorld.ToTileCoordinates();
        if (!WorldGen.InWorld(corner.X, corner.Y, 2)) {
            Main.NewText(YardSystem.Text("OutsideWorld"), Color.Orange);
            return false;
        }
        if (!selection.HasSelection || !ReferenceEquals(selection.SelectedItem, Item)) {
            selection.Begin(Item, corner);
            Main.NewText(YardSystem.Text("FirstCorner", corner.X, corner.Y), Color.LightSkyBlue);
            return true;
        }

        Rectangle source = selection.RectangleTo(corner);
        if (Main.netMode == NetmodeID.MultiplayerClient) {
            system.RequestCreate(this, player, source);
            return true;
        }
        if (YardSystem.Instance.TryCreate(source, out Yard region, out string message)) {
            binding.Bind(region, YardSystem.Instance);
            selection.Cancel();
            Main.NewText(message, Color.LightGreen);
        }
        else
            Main.NewText(message, Color.Orange);
        return true;
    }

    public override void ModifyTooltips(List<TooltipLine> tooltips)
    {
        Yard region = BoundYard;
        if (YardId == Guid.Empty)
            return;
        tooltips.Add(new TooltipLine(Mod, "YardId", "YardId: " + YardId));
        if (region != null) {
            tooltips.Add(new TooltipLine(Mod, "YardDimensions", YardSystem.Text("Dimensions",
                region.Width, region.Height, region.StorageOrigin.X, region.StorageOrigin.Y)));
            if (!region.IsLoaded)
                tooltips.Add(new TooltipLine(Mod, "Unavailable", YardSystem.Text("Unavailable")));
        }
        else
            tooltips.Add(new TooltipLine(Mod, "MissingYard", YardSystem.Text("MissingYard", YardId)));
    }

    public override void SaveData(TagCompound tag)
    {
        if (YardId != Guid.Empty)
            tag["yardId"] = YardId.ToString("D");
    }

    public override void LoadData(TagCompound tag) =>
        binding = new YardBinding(Guid.TryParse(tag.GetString("yardId"), out Guid id) ? id : Guid.Empty);

    public override void NetSend(BinaryWriter writer) => writer.Write(YardId.ToByteArray());
    public override void NetReceive(BinaryReader reader) => binding = new YardBinding(YardPacketCodec.ReadId(reader));

    public override void AddRecipes() => CreateRecipe().AddIngredient(ItemID.Wood).Register();
}
