using System;
using Microsoft.Xna.Framework;
using Terraria.ModLoader.IO;

namespace TerrariaDynamics.Common.Circuitry;

// Targets belong to a Yard in a specific world. Cells use Yard-local tile
// coordinates, never rendered positions or reusable storage addresses.
internal readonly record struct CircuitKeybindTarget(Guid WorldId, Guid YardId, Point Cell, string TileName)
{
    public TagCompound Save() => new() {
        ["worldId"] = WorldId.ToString("D"),
        ["yardId"] = YardId.ToString("D"),
        ["x"] = Cell.X,
        ["y"] = Cell.Y,
        ["tileName"] = TileName
    };

    public static bool TryLoad(TagCompound tag, out CircuitKeybindTarget target)
    {
        target = default;
        if (!Guid.TryParse(tag.GetString("worldId"), out Guid worldId) || worldId == Guid.Empty
            || !Guid.TryParse(tag.GetString("yardId"), out Guid yardId) || yardId == Guid.Empty)
            return false;
        Point cell = new(tag.GetInt("x"), tag.GetInt("y"));
        string tileName = tag.GetString("tileName");
        if (cell.X < 0 || cell.Y < 0 || string.IsNullOrWhiteSpace(tileName))
            return false;
        target = new CircuitKeybindTarget(worldId, yardId, cell, tileName);
        return true;
    }
}
