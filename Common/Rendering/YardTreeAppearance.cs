using System.IO;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;
using Terraria.ModLoader.IO;

namespace TerrariaDynamics.Common.Rendering;

// Resolved appearance, captured before ownership moves. One binding per tree
// cell handles fragments/branches without requiring the root to be selected.
internal readonly record struct YardTreeAppearance(int TreeType, int TrunkStyle, int FoliageStyle,
    int FrameOffset, int CrownWidth, int CrownHeight, string ModGround)
{
    public bool TryResolve(out YardTreeAppearance appearance)
    {
        appearance = this;
        if (string.IsNullOrEmpty(ModGround)) return true;
        if (!ModContent.TryFind<ModTile>(ModGround, out var ground)) return false;
        // Stored runtime tile IDs must not survive a changed mod load order.
        if (TreeType == TileID.Trees)
            appearance = this with { TrunkStyle = 7 + ground.Type, FoliageStyle = 100 + ground.Type };
        else if (TreeType == TileID.PalmTree)
            appearance = this with { TrunkStyle = (8 + ground.Type) * (TrunkStyle < 0 ? -1 : 1) };
        return true;
    }

    public TagCompound Save() => new() {
        ["type"] = TreeType, ["trunk"] = TrunkStyle, ["foliage"] = FoliageStyle,
        ["frameOffset"] = FrameOffset, ["width"] = CrownWidth, ["height"] = CrownHeight,
        ["modGround"] = ModGround ?? ""
    };

    public static YardTreeAppearance Load(TagCompound tag)
    {
        var result = new YardTreeAppearance(tag.GetInt("type"), tag.GetInt("trunk"), tag.GetInt("foliage"),
            tag.GetInt("frameOffset"), tag.GetInt("width"), tag.GetInt("height"), tag.GetString("modGround"));
        if (!YardTreeGeometry.IsTree(result.TreeType) || result.CrownWidth < 1 || result.CrownWidth > 4096
            || result.CrownHeight < 1 || result.CrownHeight > 4096 || result.FrameOffset < -4096 || result.FrameOffset > 4096
            || result.TrunkStyle < -65544 || result.TrunkStyle > 65544 || result.FoliageStyle < 0 || result.FoliageStyle > 65635)
            throw new InvalidDataException("Invalid Yard tree appearance.");
        return result;
    }
}
