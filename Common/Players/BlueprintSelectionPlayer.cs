using System;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ModLoader;
using TerrariaDynamics.Common.TileYards;

namespace TerrariaDynamics.Common.Players;

public sealed class BlueprintSelectionPlayer : ModPlayer
{
    public Item SelectedItem { get; private set; }
    public Point FirstCorner { get; private set; }
    public bool HasSelection => SelectedItem != null;

    public void Begin(Item item, Point corner)
    {
        SelectedItem = item;
        FirstCorner = corner;
    }

    public void Cancel() => SelectedItem = null;

    public Rectangle RectangleTo(Point corner) => new(
        Math.Min(FirstCorner.X, corner.X), Math.Min(FirstCorner.Y, corner.Y),
        Math.Abs(FirstCorner.X - corner.X) + 1, Math.Abs(FirstCorner.Y - corner.Y) + 1);

    public override void PostUpdate()
    {
        if (HasSelection && (!ReferenceEquals(Player.HeldItem, SelectedItem) || Player.dead))
            Cancel();
    }

    public override void OnEnterWorld()
    {
        Cancel();
        YardSystem.Instance.EnterNetworkWorld();
    }
}
