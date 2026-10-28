using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Xna.Framework;
using Terraria.ModLoader.IO;
using TerrariaDynamics.Common.Storage;
using TerrariaDynamics.Common.TileYards;

namespace TerrariaDynamics.Common.Persistence;

internal static class YardWorldData
{
    public const int Version = 2;

    public static void Save(TagCompound tag, IEnumerable<Yard> yards, Func<Yard, byte[]> snapshot)
    {
        tag["version"] = Version;
        tag["yards"] = yards.Select(yard => yard.Save(snapshot(yard))).ToList();
    }

    // Decode and validate the entire registry before the caller attaches anything
    // or expands storage. Touching edges are valid; intersecting rectangles aren't.
    public static List<Yard> Load(TagCompound tag, int playableWidth, int playableHeight, int storageHeight)
    {
        var yards = new List<Yard>();
        if (!tag.ContainsKey("yards") && !tag.ContainsKey("version"))
            return yards;
        if (tag.GetInt("version") != Version || !tag.ContainsKey("yards"))
            throw new InvalidDataException("Unsupported TerrariaDynamics yard format.");
        var ids = new HashSet<Guid>();
        foreach (TagCompound saved in tag.GetList<TagCompound>("yards")) {
            foreach (string key in new[] { "id", "sourceX", "sourceY", "width", "height", "storageX", "storageY", "tiles" }) {
                if (!saved.ContainsKey(key))
                    throw new InvalidDataException($"Missing Yard field: {key}.");
            }
            var source = new Rectangle(saved.GetInt("sourceX"), saved.GetInt("sourceY"),
                saved.GetInt("width"), saved.GetInt("height"));
            var origin = new Point(saved.GetInt("storageX"), saved.GetInt("storageY"));
            if (!Guid.TryParse(saved.GetString("id"), out Guid id) || id == Guid.Empty
                || source.X < 0 || source.Y < 0 || source.Width < 1 || source.Height < 1
                || (long)source.Width * source.Height > Yard.MaxSelectionArea
                || (long)source.X + source.Width > playableWidth
                || (long)source.Y + source.Height > playableHeight
                || origin.X < Math.Max(YardStorageAllocator.StartX, playableWidth) || origin.Y < 0
                || (long)origin.X + source.Width > YardStorageAllocator.CoordinateLimit
                || (long)origin.Y + source.Height > Math.Min(storageHeight, YardStorageAllocator.CoordinateLimit))
                throw new InvalidDataException("Invalid TerrariaDynamics yard region.");
            var yard = new Yard(id, source, origin) { PendingTiles = saved.GetByteArray("tiles") };
            yard.LoadMetadata(saved);
            if (!ids.Add(id) || yards.Any(other => yard.Bounds.Intersects(other.Bounds)))
                throw new InvalidDataException("Duplicate or overlapping TerrariaDynamics yard regions.");
            yards.Add(yard);
        }
        return yards;
    }
}
