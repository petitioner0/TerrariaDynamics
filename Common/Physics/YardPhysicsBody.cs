using System;
using Box2D.NET;
using Microsoft.Xna.Framework;

namespace TerrariaDynamics.Common.Physics;

// Box2D positions and vertices use tile units; the pose API converts Yard pixels.
// The body origin stays at the Yard's local top-left; Box2D computes its COM.
internal sealed class YardPhysicsBody : IDisposable
{
    private readonly B2WorldId world;
    private readonly float radius;
    public B2BodyId BodyId { get; private set; }
    public int ShapeCount { get; }
    public float Mass => B2Bodies.b2Body_GetMass(BodyId);
    public bool IsValid => B2Worlds.b2World_IsValid(world) && B2Worlds.b2Body_IsValid(BodyId);

    public YardPhysicsBody(B2WorldId world, int width, int height, ReadOnlySpan<TerrainTileShape> shapes,
        Vector2 position, float rotation, Vector2 linearVelocity, float angularVelocity)
    {
        this.world = world;
        radius = MathF.Sqrt((float)width * width + (float)height * height);
        B2BodyDef definition = B2Types.b2DefaultBodyDef();
        definition.type = B2BodyType.b2_dynamicBody;
        definition.position = PhysicsSystem.PixelsToPhysics(position);
        definition.rotation = Rotation(rotation);
        BodyId = B2Bodies.b2CreateBody(world, definition);
        try {
            B2ShapeDef shapeDef = B2Types.b2DefaultShapeDef();
            shapeDef.updateBodyMass = false;
            ShapeCount = TileCollisionBuilder.CreateShapes(BodyId, width, height, shapes,
                new bool[shapes.Length], shapeDef);
            if (ShapeCount > 0)
                B2Bodies.b2Body_ApplyMassFromShapes(BodyId);
            else {
                // Pure walls/wires/liquids still have a movable visual pose, but
                // no fabricated collider. Give the empty body a unit point mass.
                B2Bodies.b2Body_SetMassData(BodyId, new B2MassData { mass = 1 });
            }
            // Apply saved velocities after mass computation, which can shift COM.
            B2Bodies.b2Body_SetLinearVelocity(BodyId, PhysicsSystem.PixelsToPhysics(linearVelocity));
            B2Bodies.b2Body_SetAngularVelocity(BodyId, angularVelocity);
        }
        catch {
            Dispose();
            throw;
        }
    }

    public void SetTransform(Vector2 position, float rotation)
    {
        B2Bodies.b2Body_SetTransform(BodyId, PhysicsSystem.PixelsToPhysics(position), Rotation(rotation));
        B2Bodies.b2Body_SetAwake(BodyId, true);
    }

    public Vector2 PreservePointVelocity(YardPhysicsBody previous, Vector2 velocity, float angularVelocity)
    {
        Vector2 shift = PhysicsSystem.PhysicsToPixels(B2Bodies.b2Body_GetWorldCenterOfMass(BodyId))
            - PhysicsSystem.PhysicsToPixels(B2Bodies.b2Body_GetWorldCenterOfMass(previous.BodyId));
        Vector2 next = velocity + new Vector2(-shift.Y, shift.X) * angularVelocity;
        B2Bodies.b2Body_SetLinearVelocity(BodyId, PhysicsSystem.PixelsToPhysics(next));
        return next;
    }

    public void GetState(out Vector2 position, out float rotation, out Vector2 velocity, out float angularVelocity)
    {
        B2Transform transform = B2Bodies.b2Body_GetTransform(BodyId);
        position = PhysicsSystem.PhysicsToPixels(transform.p);
        rotation = MathF.Atan2(transform.q.s, transform.q.c);
        velocity = PhysicsSystem.PhysicsToPixels(B2Bodies.b2Body_GetLinearVelocity(BodyId));
        angularVelocity = B2Bodies.b2Body_GetAngularVelocity(BodyId);
    }

    // Force uses Box2D mass * tiles/s², and acts at its computed world COM.
    public void ApplyForceToCenter(Vector2 force) =>
        B2Bodies.b2Body_ApplyForceToCenter(BodyId, new B2Vec2(force.X, force.Y), true);

    public void ApplyForceAtWorldPosition(Vector2 force, Vector2 worldPosition) =>
        B2Bodies.b2Body_ApplyForce(BodyId, new B2Vec2(force.X, force.Y),
            PhysicsSystem.PixelsToPhysics(worldPosition), true);

    public B2AABB TerrainBounds(float timeStep, float gravity)
    {
        B2AABB bounds = B2Bodies.b2Body_ComputeAABB(BodyId);
        B2Vec2 velocity = B2Bodies.b2Body_GetLinearVelocity(BodyId);
        float angularTravel = MathF.Abs(B2Bodies.b2Body_GetAngularVelocity(BodyId)) * radius * timeStep;
        float marginX = 2 + MathF.Abs(velocity.X) * timeStep + angularTravel;
        float marginY = 2 + MathF.Abs(velocity.Y) * timeStep + angularTravel + gravity * timeStep * timeStep;
        bounds.lowerBound.X -= marginX;
        bounds.lowerBound.Y -= marginY;
        bounds.upperBound.X += marginX;
        bounds.upperBound.Y += marginY;
        return bounds;
    }

    public void Dispose()
    {
        if (IsValid)
            B2Bodies.b2DestroyBody(BodyId);
        BodyId = default;
    }

    private static B2Rot Rotation(float angle) => new(MathF.Cos(angle), MathF.Sin(angle));
}
