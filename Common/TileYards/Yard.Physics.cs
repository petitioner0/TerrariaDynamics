using System;
using Box2D.NET;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using TerrariaDynamics.Common.Physics;

namespace TerrariaDynamics.Common.TileYards;

public sealed partial class Yard
{
    // The optional runtime body belongs to this instance. Clients keep only the
    // authoritative state below; the shared Box2D world belongs to PhysicsSystem.
    private YardPhysicsBody physicsBody;
    public Vector2 WorldPosition { get; private set; }
    public float Rotation { get; private set; }
    // Linear velocity is pixels/second; angular velocity is radians/second.
    public Vector2 LinearVelocity { get; private set; }
    public float AngularVelocity { get; private set; }
    public bool HasPhysicsBody => physicsBody != null && physicsBody.IsValid;
    internal float PhysicsMass => HasPhysicsBody ? physicsBody.Mass : 0;

    public bool TryGetPhysicsBody(out B2BodyId body)
    {
        body = default;
        if (!HasPhysicsBody)
            return false;
        body = physicsBody.BodyId;
        return true;
    }

    // The host supplies the world and tile map while holding its save/transfer
    // lock. Construction commits only after every collision shape is ready.
    internal void CreatePhysics(B2WorldId world, Tilemap tiles, bool fromSource)
    {
        ThrowIfInvalid();
        if (Main.netMode == NetmodeID.MultiplayerClient)
            return;
        if (physicsBody != null)
            throw new InvalidOperationException("Yard already has a physics body.");
        var shapes = new TerrainTileShape[Width * Height];
        Point origin = fromSource ? Source.Location : StorageOrigin;
        for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
                shapes[y * Width + x] = TileCollisionRules.ShapeOf(tiles[origin.X + x, origin.Y + y]);
        physicsBody = new YardPhysicsBody(world, Width, Height, shapes,
            WorldPosition, Rotation, LinearVelocity, AngularVelocity);
    }

    internal void ReleasePhysics()
    {
        physicsBody?.Dispose();
        physicsBody = null;
    }

    internal void RefreshPhysics(B2WorldId world)
    {
        if (Main.netMode == NetmodeID.MultiplayerClient || !HasPhysicsBody)
            return;
        var shapes = new TerrainTileShape[Width * Height];
        for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
                shapes[y * Width + x] = TileCollisionRules.ShapeOf(Main.tile[StorageOrigin.X + x, StorageOrigin.Y + y]);
        // Construct first: a failed replacement leaves the old body intact.
        var replacement = new YardPhysicsBody(world, Width, Height, shapes,
            WorldPosition, Rotation, LinearVelocity, AngularVelocity, physicsBody.IsStatic);
        YardPhysicsBody previous = physicsBody;
        LinearVelocity = replacement.PreservePointVelocity(previous, LinearVelocity, AngularVelocity);
        physicsBody = replacement;
        previous.Dispose();
    }

    internal void SetPhysicsStatic(bool isStatic)
    {
        if (Main.netMode == NetmodeID.MultiplayerClient || !IsLoaded || !HasPhysicsBody
            || physicsBody.IsStatic == isStatic)
            return;
        physicsBody.SetStatic(isStatic);
        LinearVelocity = Vector2.Zero;
        AngularVelocity = 0;
    }

    public void SetTransform(Vector2 worldPosition, float rotation)
    {
        ThrowIfInvalid();
        if (!float.IsFinite(worldPosition.X) || !float.IsFinite(worldPosition.Y) || !float.IsFinite(rotation))
            throw new ArgumentOutOfRangeException(nameof(worldPosition));
        if (HasPhysicsBody)
            physicsBody.SetTransform(worldPosition, rotation);
        WorldPosition = worldPosition;
        Rotation = rotation;
        renderInterpolator = null;
    }

    // Simulation and authoritative network results do not teleport the body back
    // into Box2D. Its origin is the local top-left, not its center of mass.
    internal void ApplyPhysicsState(Vector2 position, float rotation, Vector2 velocity, float angularVelocity)
    {
        ThrowIfInvalid();
        if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(rotation)
            || !float.IsFinite(velocity.X) || !float.IsFinite(velocity.Y) || !float.IsFinite(angularVelocity))
            throw new ArgumentOutOfRangeException(nameof(position));
        WorldPosition = position;
        Rotation = rotation;
        LinearVelocity = velocity;
        AngularVelocity = angularVelocity;
    }

    internal void SynchronizePhysicsState()
    {
        if (!IsLoaded || !HasPhysicsBody)
            return;
        physicsBody.GetState(out Vector2 position, out float rotation,
            out Vector2 velocity, out float angularVelocity);
        ApplyPhysicsState(position, rotation, velocity, angularVelocity);
    }

    internal void ApplyForceToCenter(Vector2 force)
    {
        ThrowIfInvalid();
        if (!float.IsFinite(force.X) || !float.IsFinite(force.Y))
            throw new ArgumentOutOfRangeException(nameof(force));
        if (Main.netMode != NetmodeID.MultiplayerClient && IsLoaded && HasPhysicsBody)
            physicsBody.ApplyForceToCenter(force);
    }

    // Force is in Box2D mass * tiles/s²; its application point is world pixels.
    internal void ApplyForceAtWorldPosition(Vector2 force, Vector2 worldPosition)
    {
        ThrowIfInvalid();
        if (!float.IsFinite(force.X) || !float.IsFinite(force.Y)
            || !float.IsFinite(worldPosition.X) || !float.IsFinite(worldPosition.Y))
            throw new ArgumentOutOfRangeException(nameof(force));
        if (Main.netMode != NetmodeID.MultiplayerClient && IsLoaded && HasPhysicsBody)
            physicsBody.ApplyForceAtWorldPosition(force, worldPosition);
    }

    internal bool TryGetPhysicsTerrainBounds(float timeStep, float gravity, out B2AABB bounds)
    {
        bounds = default;
        if (!IsLoaded || !HasPhysicsBody || physicsBody.ShapeCount == 0)
            return false;
        bounds = physicsBody.TerrainBounds(timeStep, gravity);
        return true;
    }
}
