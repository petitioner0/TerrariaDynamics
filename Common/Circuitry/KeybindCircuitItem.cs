using Terraria.ModLoader;

namespace TerrariaDynamics.Common.Circuitry;

/// <summary>
/// Reusable placement item for a keybind-enabled circuit tile. Concrete items
/// supply their own texture, recipes and other defaults.
/// </summary>
public abstract class KeybindCircuitItem<TTile> : ModItem where TTile : KeybindCircuitTile
{
    public override void SetDefaults() => Item.DefaultToPlaceableTile(ModContent.TileType<TTile>());
}
