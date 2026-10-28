using Microsoft.Xna.Framework;
using Terraria.ModLoader;
using TerrariaDynamics.Common.TileYards;

namespace TerrariaDynamics.Content.Commands;

public sealed class YardCommand : ModCommand
{
    public override string Command => "yard";
    public override CommandType Type => CommandType.Chat;
    public override string Usage => "/yard [list|render]";
    public override string Description => "Inspect blueprint yards or toggle their rendering.";

    public override void Action(CommandCaller caller, string input, string[] args)
    {
        YardSystem system = YardSystem.Instance;
        if (args.Length == 1 && args[0].Equals("render", System.StringComparison.OrdinalIgnoreCase)) {
            system.RenderEnabled = !system.RenderEnabled;
            caller.Reply(YardSystem.Text(system.RenderEnabled ? "RenderOn" : "RenderOff"), Color.LightSkyBlue);
            return;
        }
        if (args.Length > 1 || (args.Length == 1 && !args[0].Equals("list", System.StringComparison.OrdinalIgnoreCase))) {
            caller.Reply(Usage, Color.Orange);
            return;
        }
        caller.Reply(YardSystem.Text("YardCount", system.Regions.Count), Color.LightSkyBlue);
        foreach (Yard region in system.Regions)
            caller.Reply(YardSystem.Text("YardInfo", region.Id, region.Bounds.X, region.Bounds.Y,
                region.Width, region.Height) + (region.IsLoaded ? "" : " " + YardSystem.Text("Unavailable")));
    }
}
