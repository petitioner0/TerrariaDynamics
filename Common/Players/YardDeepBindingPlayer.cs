using System;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.DataStructures;
using Terraria.ModLoader;
using TerrariaDynamics.Common.TileYards;

namespace TerrariaDynamics.Common.Players;

// Interactions own movement, posture and their network protocol. This state
// supplies their common reference frame without imposing a contact constraint.
public sealed class YardDeepBindingPlayer : ModPlayer
{
    private Yard yard;
    public Yard BoundYard => yard is { IsLoaded: true } ? yard : null;
    public bool IsBound => BoundYard != null;
    public Vector2 LocalAnchor { get; private set; }
    internal uint Revision { get; private set; }

    public void Bind(Yard target, Vector2 localAnchor)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!target.IsLoaded || !YardSystem.Instance.TryGet(target.Id, out Yard current)
            || !ReferenceEquals(target, current))
            throw new ArgumentException("Deep binding requires a loaded Yard in this world.", nameof(target));
        if (!float.IsFinite(localAnchor.X) || !float.IsFinite(localAnchor.Y))
            throw new ArgumentException("The interaction anchor must be finite.", nameof(localAnchor));
        Clear();
        yard = target;
        LocalAnchor = localAnchor;
        yard.Invalidated += Unbind;
        Player.GetModPlayer<YardMovementPlayer>().SuspendForRest();
    }

    // An old interaction may not clear a subsequent binding to another Yard.
    public void Unbind(Yard expected)
    {
        if (ReferenceEquals(yard, expected))
            Clear();
    }

    internal void Clear()
    {
        if (yard != null)
            yard.Invalidated -= Unbind;
        yard = null;
        LocalAnchor = Vector2.Zero;
        Revision++;
    }

    public override void OnEnterWorld() => Clear();
    public override void PlayerDisconnect() => Clear();
    public override void UpdateDead() => Clear();

    public override void ModifyDrawInfo(ref PlayerDrawSet drawInfo)
    {
        if (BoundYard is not Yard bound || drawInfo.headOnlyRender)
            return;
        Vector2 offset = bound.RenderCoordinates.LocalToWorld(LocalAnchor) - Player.Bottom;
        drawInfo.Position += offset;
        drawInfo.ItemLocation += offset;
    }

    public override void TransformDrawData(ref PlayerDrawSet drawInfo)
    {
        if (BoundYard is not Yard bound || drawInfo.headOnlyRender)
            return;
        // Vanilla has already applied sitting/sleeping and seat offsets. Rotate
        // that complete posture, leaving the original player hitbox untouched.
        YardCoordinates pose = bound.RenderCoordinates;
        Vector2 pivot = pose.LocalToWorld(LocalAnchor) - Main.screenPosition;
        for (int i = 0; i < drawInfo.DrawDataCache.Count; i++) {
            DrawData data = drawInfo.DrawDataCache[i];
            if (data.ignorePlayerRotation)
                continue;
            data.position = pivot + pose.LocalVectorToWorld(data.position - pivot);
            data.rotation += pose.Rotation;
            drawInfo.DrawDataCache[i] = data;
        }
    }
}
