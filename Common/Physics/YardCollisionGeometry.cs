using System;
using System.Collections.Generic;
using Box2D.NET;
using Microsoft.Xna.Framework;
using TerrariaDynamics.Common.TileYards;

namespace TerrariaDynamics.Common.Physics;

internal readonly record struct YardPlayerContact(Vector2 Normal, float Separation);
internal readonly record struct YardPlayerCast(Vector2 Normal, float Fraction);

// Geometry queries never create a player body or apply an impulse to a Yard.
// The local cache also works on clients without a simulated world. Platforms
// are player-only one-way surfaces; they never become Yard rigid-body shapes.
internal sealed class YardCollisionGeometry
{
    public const float ContactTolerance = 0.5f;
    private const int BucketSize = 16;
    private readonly List<Shape> polygons = new();
    private readonly Dictionary<Point, List<int>> buckets = new();
    private readonly HashSet<int> candidates = new();
    private readonly Dictionary<Point, Platform> platforms = new();
    public Vector2 LocalCenterOfMass { get; }

    public YardCollisionGeometry(int width, int height, ReadOnlySpan<TerrainTileShape> tiles,
        ReadOnlySpan<TerrainTileShape> oneWayTiles = default)
    {
        TileCollisionBuilder.BuildPolygons(width, height, tiles, new bool[tiles.Length], Add);
        float areaSum = 0;
        Vector2 centerSum = Vector2.Zero;
        foreach (Shape shape in polygons) {
            B2Polygon polygon = shape.Polygon;
            float area = 0;
            for (int i = 0; i < polygon.count; i++) {
                B2Vec2 a = polygon.vertices[i];
                B2Vec2 b = polygon.vertices[(i + 1) % polygon.count];
                area += a.X * b.Y - a.Y * b.X;
            }
            area = MathF.Abs(area) * 0.5f;
            areaSum += area;
            centerSum += PhysicsSystem.PhysicsToPixels(polygon.centroid) * area;
        }
        LocalCenterOfMass = areaSum > 0 ? centerSum / areaSum : Vector2.Zero;
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                if (oneWayTiles.Length > y * width + x && oneWayTiles[y * width + x] != TerrainTileShape.None) {
                    TerrainTileShape shape = oneWayTiles[y * width + x];
                    Vector2 a = new(x * YardCoordinates.TileSize, y * YardCoordinates.TileSize);
                    Vector2 b = a + new Vector2(YardCoordinates.TileSize, 0);
                    if (shape == TerrainTileShape.DownRight)
                        b.Y += YardCoordinates.TileSize;
                    else if (shape == TerrainTileShape.DownLeft)
                        a.Y += YardCoordinates.TileSize;
                    else if (shape == TerrainTileShape.HalfBlock) {
                        a.Y += YardCoordinates.TileSize / 2;
                        b.Y += YardCoordinates.TileSize / 2;
                    }
                    Vector2 tangent = Vector2.Normalize(b - a);
                    platforms.Add(new Point(x, y), new Platform(a, b, tangent, new Vector2(tangent.Y, -tangent.X)));
                }
    }

    public bool TryContact(YardCoordinates pose, Vector2 position, Vector2 size,
        out YardPlayerContact contact, bool penetrationOnly = false)
    {
        contact = default;
        Query(pose, position, size, Vector2.Zero);
        B2Polygon player = PlayerBox(size);
        // Expand only the query, not the actual player collision box. This keeps
        // touching surfaces detectable even when the cast stops a little short.
        player.radius = ContactTolerance / PhysicsSystem.PixelsPerUnit;
        B2Transform yardTransform = Transform(pose.WorldOrigin, pose.Rotation);
        B2Transform playerTransform = Transform(position + size / 2, 0);
        float best = float.PositiveInfinity;
        bool found = false;
        foreach (int index in candidates) {
            B2Polygon polygon = polygons[index].Polygon;
            B2Manifold manifold = B2Manifolds.b2CollidePolygons(ref polygon, in yardTransform,
                ref player, in playerTransform);
            for (int i = 0; i < manifold.pointCount; i++) {
                float separation = manifold.points[i].separation * PhysicsSystem.PixelsPerUnit + ContactTolerance;
                if (separation > ContactTolerance || (penetrationOnly && separation >= -0.01f))
                    continue;
                if (separation < best) {
                    best = separation;
                    contact = new YardPlayerContact(new Vector2(manifold.normal.X, manifold.normal.Y), separation);
                    found = true;
                }
            }
        }
        return found;
    }

    public bool TryCast(YardCoordinates pose, Vector2 position, Vector2 size, Vector2 movement,
        out YardPlayerCast hit, bool fallThrough = false, float gravityDirection = 1, float endContactTolerance = 0)
    {
        hit = default;
        if (movement.LengthSquared() < 0.000001f)
            return false;
        Query(pose, position, size, movement);
        B2Polygon player = PlayerBox(size);
        B2ShapeCastPairInput input = new() {
            proxyB = B2Distances.b2MakeProxy(player.vertices.AsSpan(), player.count, 0),
            transformA = Transform(pose.WorldOrigin, pose.Rotation),
            transformB = Transform(position + size / 2, 0),
            translationB = PhysicsSystem.PixelsToPhysics(movement),
            maxFraction = 1
        };
        player.radius = ContactTolerance / PhysicsSystem.PixelsPerUnit;
        float first = 1;
        bool found = false;
        foreach (int index in candidates) {
            B2Polygon polygon = polygons[index].Polygon;
            B2Manifold contact = B2Manifolds.b2CollidePolygons(ref polygon, in input.transformA,
                ref player, in input.transformB);
            Vector2 contactNormal = new(contact.normal.X, contact.normal.Y);
            float inward = -Vector2.Dot(movement, contactNormal);
            // Box2D casts deliberately omit initial overlaps/touching. Handle
            // near contacts explicitly so resting gravity cannot step through
            // a floor, and a zero-distance foot probe still reports support.
            if (inward > 0.0001f) {
                for (int i = 0; i < contact.pointCount; i++) {
                    float distance = contact.points[i].separation * PhysicsSystem.PixelsPerUnit + ContactTolerance;
                    float fraction = MathF.Max(0, distance) / inward;
                    if (distance <= ContactTolerance && fraction <= first
                        && !AllowedEndContact(movement, contactNormal, fraction, endContactTolerance)) {
                        first = fraction;
                        hit = new YardPlayerCast(contactNormal, fraction);
                        found = true;
                    }
                }
            }
            input.proxyA = polygons[index].Proxy;
            B2CastOutput result = B2Distances.b2ShapeCast(in input);
            Vector2 normal = new(result.normal.X, result.normal.Y);
            if (!result.hit || result.fraction > first || Vector2.Dot(movement, normal) >= -0.0001f
                || AllowedEndContact(movement, normal, result.fraction, endContactTolerance))
                continue;
            first = result.fraction;
            hit = new YardPlayerCast(normal, result.fraction);
            found = true;
        }
        if (!fallThrough && TryCastPlatforms(pose, position, size, movement, gravityDirection,
                out YardPlayerCast platform, endContactTolerance)
            && (!found || platform.Fraction < hit.Fraction)) {
            hit = platform;
            found = true;
        }
        return found;
    }

    // Furniture snaps may finish touching a floor despite shape-cast slop.
    // Filter each shape before selecting the first hit, so an allowed floor
    // contact cannot hide a wall farther along the same path.
    private static bool AllowedEndContact(Vector2 movement, Vector2 normal, float fraction, float tolerance) =>
        tolerance > 0 && -Vector2.Dot(movement, normal) * (1 - fraction) <= tolerance;

    private bool TryCastPlatforms(YardCoordinates pose, Vector2 position, Vector2 size,
        Vector2 movement, float gravityDirection, out YardPlayerCast hit, float endContactTolerance)
    {
        hit = default;
        Vector2 center = pose.WorldToLocal(position + size / 2);
        Vector2 delta = pose.WorldVectorToLocal(movement);
        Vector2 localX = pose.WorldVectorToLocal(new Vector2(size.X / 2, 0));
        Vector2 localY = pose.WorldVectorToLocal(new Vector2(0, size.Y / 2));
        Rectangle area = pose.VisibleStorageArea(Vector2.Min(position, position + movement),
            Vector2.Max(position + size, position + size + movement), ContactTolerance + 1);
        float first = 1;
        bool found = false;
        for (int y = area.Top - pose.StorageOrigin.Y; y < area.Bottom - pose.StorageOrigin.Y; y++) {
            for (int x = area.Left - pose.StorageOrigin.X; x < area.Right - pose.StorageOrigin.X; x++) {
                if (!platforms.TryGetValue(new Point(x, y), out Platform platform))
                    continue;
                Vector2 normal = pose.LocalVectorToWorld(platform.Normal);
                float inward = -Vector2.Dot(delta, platform.Normal);
                // The local top stays the collidable side. Steep platforms do
                // not become walls; slopes retain their own support plane.
                if (normal.Y * gravityDirection >= -0.64f || inward <= 0.0001f)
                    continue;
                float projectionX = Vector2.Dot(localX, platform.Normal);
                float projectionY = Vector2.Dot(localY, platform.Normal);
                float distance = Vector2.Dot(center - platform.A, platform.Normal)
                    - MathF.Abs(projectionX) - MathF.Abs(projectionY);
                // Test the previous support side: jumping up from below and
                // existing penetration must never be pulled onto a platform.
                if (distance < -ContactTolerance)
                    continue;
                float fraction = MathF.Max(0, distance) / inward;
                if (fraction > first || AllowedEndContact(movement, normal, fraction, endContactTolerance))
                    continue;
                // At the support plane the lowest corner/edge is narrower than
                // the inverse AABB. Use that edge, avoiding false edge landings.
                Vector2 support = center + delta * fraction;
                float extent = 0;
                if (MathF.Abs(projectionX) < 0.0001f)
                    extent += MathF.Abs(Vector2.Dot(localX, platform.Tangent));
                else
                    support -= MathF.Sign(projectionX) * localX;
                if (MathF.Abs(projectionY) < 0.0001f)
                    extent += MathF.Abs(Vector2.Dot(localY, platform.Tangent));
                else
                    support -= MathF.Sign(projectionY) * localY;
                float along = Vector2.Dot(support - platform.A, platform.Tangent);
                if (along + extent < 0 || along - extent > Vector2.Distance(platform.A, platform.B))
                    continue;
                first = fraction;
                hit = new YardPlayerCast(normal, fraction);
                found = true;
            }
        }
        return found;
    }

    // Exact AABB/rotated-rectangle SAT, including all of the player box. A
    // center test or inverse-transformed AABB alone would detach too soon or
    // retain players in the empty corners of a rotated world-space AABB.
    public static bool IntersectsBounds(YardCoordinates pose, Vector2 position, Vector2 size, float margin = 1)
    {
        Vector2 yardHalf = new(pose.Size.X * YardCoordinates.TileSize / 2f,
            pose.Size.Y * YardCoordinates.TileSize / 2f);
        Vector2 playerHalf = size / 2;
        Vector2 center = position + playerHalf;
        Vector2 localCenter = pose.WorldToLocal(center) - yardHalf;
        float cos = MathF.Abs(MathF.Cos(pose.Rotation));
        float sin = MathF.Abs(MathF.Sin(pose.Rotation));
        if (MathF.Abs(localCenter.X) > yardHalf.X + cos * playerHalf.X + sin * playerHalf.Y + margin
            || MathF.Abs(localCenter.Y) > yardHalf.Y + sin * playerHalf.X + cos * playerHalf.Y + margin)
            return false;
        Vector2 worldDelta = center - pose.LocalToWorld(yardHalf);
        return MathF.Abs(worldDelta.X) <= playerHalf.X + cos * yardHalf.X + sin * yardHalf.Y + margin
            && MathF.Abs(worldDelta.Y) <= playerHalf.Y + sin * yardHalf.X + cos * yardHalf.Y + margin;
    }

    private void Add(B2Polygon polygon)
    {
        int index = polygons.Count;
        polygons.Add(new Shape(polygon, B2Distances.b2MakeProxy(polygon.vertices.AsSpan(), polygon.count, polygon.radius)));
        Vector2 min = new(float.PositiveInfinity);
        Vector2 max = new(float.NegativeInfinity);
        for (int i = 0; i < polygon.count; i++) {
            Vector2 vertex = new(polygon.vertices[i].X, polygon.vertices[i].Y);
            min = Vector2.Min(min, vertex);
            max = Vector2.Max(max, vertex);
        }
        for (int y = (int)MathF.Floor(min.Y / BucketSize); y <= (int)MathF.Floor(max.Y / BucketSize); y++) {
            for (int x = (int)MathF.Floor(min.X / BucketSize); x <= (int)MathF.Floor(max.X / BucketSize); x++) {
                Point key = new(x, y);
                if (!buckets.TryGetValue(key, out List<int> bucket))
                    buckets.Add(key, bucket = new List<int>());
                bucket.Add(index);
            }
        }
    }

    private void Query(YardCoordinates pose, Vector2 position, Vector2 size, Vector2 movement)
    {
        candidates.Clear();
        Vector2 worldMin = Vector2.Min(position, position + movement);
        Vector2 worldMax = Vector2.Max(position + size, position + size + movement);
        Rectangle area = pose.VisibleStorageArea(worldMin, worldMax, ContactTolerance + 1);
        if (area.Width == 0 || area.Height == 0)
            return;
        int left = (area.Left - pose.StorageOrigin.X) / BucketSize;
        int right = (area.Right - pose.StorageOrigin.X) / BucketSize;
        int top = (area.Top - pose.StorageOrigin.Y) / BucketSize;
        int bottom = (area.Bottom - pose.StorageOrigin.Y) / BucketSize;
        for (int y = top; y <= bottom; y++)
            for (int x = left; x <= right; x++)
                if (buckets.TryGetValue(new Point(x, y), out List<int> bucket))
                    foreach (int index in bucket)
                        candidates.Add(index);
    }

    private static B2Polygon PlayerBox(Vector2 size) =>
        B2Geometries.b2MakeBox(size.X / (2 * PhysicsSystem.PixelsPerUnit), size.Y / (2 * PhysicsSystem.PixelsPerUnit));

    private static B2Transform Transform(Vector2 position, float rotation) =>
        new(PhysicsSystem.PixelsToPhysics(position), new B2Rot(MathF.Cos(rotation), MathF.Sin(rotation)));

    private readonly record struct Shape(B2Polygon Polygon, B2ShapeProxy Proxy);
    private readonly record struct Platform(Vector2 A, Vector2 B, Vector2 Tangent, Vector2 Normal);
}
