using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ModLoader.IO;
using TerrariaDynamics.Common.TileYards;

namespace TerrariaDynamics.Common.Networking;

// Vanilla section packets do not describe objects outside the playable world.
// Send the authoritative directory, with inventories for stored/returned chests.
// Normal world chest inventories still use vanilla's open-chest synchronization.
internal sealed class YardNetworkObjects
{
    private readonly Dictionary<int, Chest> chests = new();
    private readonly Dictionary<int, Sign> signs = new();

    public static void Save(TagCompound tag, IEnumerable<Yard> yards, Rectangle returnedBounds = default) =>
        Save(tag, yards, Main.chest, Main.sign, returnedBounds);

    internal static void Save(TagCompound tag, IEnumerable<Yard> yards, Chest[] chestRegistry,
        Sign[] signRegistry, Rectangle returnedBounds = default)
    {
        Rectangle[] storage = yards.Select(yard => yard.Bounds).ToArray();
        var savedChests = new List<TagCompound>();
        for (int i = 0; i < chestRegistry.Length; i++) {
            Chest chest = chestRegistry[i];
            if (chest == null) continue;
            var saved = new TagCompound {
                ["index"] = i, ["x"] = chest.x, ["y"] = chest.y, ["name"] = chest.name ?? ""
            };
            if (storage.Any(bounds => bounds.Contains(chest.x, chest.y))
                || returnedBounds.Contains(chest.x, chest.y))
                saved["items"] = chest.item.Select(item => ItemIO.Save(item ?? new Item())).ToList();
            savedChests.Add(saved);
        }
        tag["chests"] = savedChests;
        tag["signs"] = signRegistry.Select((sign, index) => (sign, index))
            .Where(entry => entry.sign != null)
            .Select(entry => new TagCompound {
                ["index"] = entry.index, ["x"] = entry.sign.x, ["y"] = entry.sign.y,
                ["text"] = entry.sign.text ?? ""
            }).ToList();
    }

    public static YardNetworkObjects Load(TagCompound tag, int storageWidth, int storageHeight) =>
        Load(tag, storageWidth, storageHeight, Main.chest.Length, Main.sign.Length);

    internal static YardNetworkObjects Load(TagCompound tag, int storageWidth, int storageHeight,
        int chestCapacity, int signCapacity)
    {
        if (!tag.ContainsKey("chests") || !tag.ContainsKey("signs"))
            throw new InvalidDataException("Missing Yard object directory.");
        var result = new YardNetworkObjects();
        foreach (TagCompound saved in tag.GetList<TagCompound>("chests")) {
            int index = Validate(saved, chestCapacity, storageWidth, storageHeight);
            var chest = new Chest { x = saved.GetInt("x"), y = saved.GetInt("y"), name = saved.GetString("name") };
            if (saved.ContainsKey("items")) {
                IList<TagCompound> items = saved.GetList<TagCompound>("items");
                if (items.Count != Chest.maxItems)
                    throw new InvalidDataException("Invalid Yard chest inventory.");
                chest.item = items.Select(ItemIO.Load).ToArray();
            }
            else
                chest.item = null;
            if (!result.chests.TryAdd(index, chest))
                throw new InvalidDataException("Duplicate Yard chest index.");
        }
        foreach (TagCompound saved in tag.GetList<TagCompound>("signs")) {
            int index = Validate(saved, signCapacity, storageWidth, storageHeight);
            if (!result.signs.TryAdd(index, new Sign {
                x = saved.GetInt("x"), y = saved.GetInt("y"), text = saved.GetString("text")
            }))
                throw new InvalidDataException("Duplicate Yard sign index.");
        }
        return result;
    }

    private static int Validate(TagCompound saved, int capacity, int width, int height)
    {
        if (!saved.ContainsKey("index") || !saved.ContainsKey("x") || !saved.ContainsKey("y")
            || (uint)saved.GetInt("index") >= capacity || (uint)saved.GetInt("x") >= width
            || (uint)saved.GetInt("y") >= height)
            throw new InvalidDataException("Invalid Yard object address.");
        return saved.GetInt("index");
    }

    public void Apply() => Apply(Main.chest, Main.sign, Main.player, Main.myPlayer);

    internal void Apply(Chest[] chestRegistry, Sign[] signRegistry, Player[] players, int localPlayer)
    {
        for (int i = 0; i < chestRegistry.Length; i++) {
            Chest old = chestRegistry[i];
            chests.TryGetValue(i, out Chest incoming);
            if (old != null && (incoming == null || old.x != incoming.x || old.y != incoming.y)) {
                foreach (Player player in players)
                    if (player != null && player.chest == i) player.chest = -1;
            }
            if (incoming != null) {
                incoming.item ??= old?.x == incoming.x && old.y == incoming.y ? old.item : new Chest().item;
            }
            chestRegistry[i] = incoming;
        }
        for (int i = 0; i < signRegistry.Length; i++) {
            Sign old = signRegistry[i];
            signs.TryGetValue(i, out Sign incoming);
            if (old != null && (incoming == null || old.x != incoming.x || old.y != incoming.y)) {
                foreach (Player player in players) {
                    if (player == null || player.sign != i) continue;
                    player.sign = -1;
                    if (player.whoAmI == localPlayer) Main.editSign = false;
                }
            }
            signRegistry[i] = incoming;
        }
    }
}
