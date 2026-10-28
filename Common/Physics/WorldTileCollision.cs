using System;
using System.Collections.Generic;
using Box2D.NET;

namespace TerrariaDynamics.Common.Physics;

// Derived, bounded terrain cache. Chunk origins are world tile coordinates;
// shape vertices are chunk-local units, never Yard backing-store addresses.
internal sealed class WorldTileCollision
{
    public const int ChunkSize = 32;
    public const int PlayerRadius = 128;
    private const int ChunkArea = ChunkSize * ChunkSize;
    private readonly Dictionary<(int X, int Y), CollisionChunk> chunks = new();
    private readonly HashSet<(int X, int Y)> requiredChunks = new();
    private readonly List<(int X, int Y)> expiredChunks = new();
    private readonly TerrainTileShape[] scanBuffer = new TerrainTileShape[ChunkArea];
    private readonly bool[] merged = new bool[ChunkArea];

    public int ChunkCount => chunks.Count;
    public int BodyCount { get; private set; }
    public int ShapeCount { get; private set; }

    public void Synchronize(B2WorldId world, int worldWidth, int worldHeight,
        IReadOnlyList<B2Vec2> playerCenters, Func<int, int, TerrainTileShape> readShape,
        IReadOnlyList<B2AABB> bodyBounds = null)
    {
        requiredChunks.Clear();
        if (worldWidth > 0 && worldHeight > 0) {
            foreach (B2Vec2 center in playerCenters) {
                if (!float.IsFinite(center.X) || !float.IsFinite(center.Y))
                    continue;
                RequireArea(center.X - PlayerRadius, center.Y - PlayerRadius,
                    center.X + PlayerRadius, center.Y + PlayerRadius, worldWidth, worldHeight);
            }
            if (bodyBounds != null)
                foreach (B2AABB bounds in bodyBounds)
                    RequireArea(bounds.lowerBound.X, bounds.lowerBound.Y,
                        bounds.upperBound.X, bounds.upperBound.Y, worldWidth, worldHeight);
        }

        expiredChunks.Clear();
        foreach (var key in chunks.Keys)
            if (!requiredChunks.Contains(key))
                expiredChunks.Add(key);
        foreach (var key in expiredChunks) {
            CollisionChunk chunk = chunks[key];
            RemoveBody(chunk);
            chunks.Remove(key);
        }

        foreach (var key in requiredChunks) {
            if (!chunks.TryGetValue(key, out CollisionChunk chunk)) {
                chunk = new CollisionChunk();
                chunks.Add(key, chunk);
            }
            int originX = key.X * ChunkSize;
            int originY = key.Y * ChunkSize;
            for (int y = 0; y < ChunkSize; y++) {
                for (int x = 0; x < ChunkSize; x++) {
                    int worldX = originX + x, worldY = originY + y;
                    scanBuffer[y * ChunkSize + x] = worldX < worldWidth && worldY < worldHeight
                        ? readShape(worldX, worldY) : TerrainTileShape.None;
                }
            }
            if (!scanBuffer.AsSpan().SequenceEqual(chunk.Shapes))
                Rebuild(world, chunk, originX, originY);
        }
    }

    private void RequireArea(float minX, float minY, float maxX, float maxY, int worldWidth, int worldHeight)
    {
        if (!float.IsFinite(minX) || !float.IsFinite(minY) || !float.IsFinite(maxX) || !float.IsFinite(maxY)
            || maxX < 0 || maxY < 0 || minX >= worldWidth || minY >= worldHeight)
            return;
        int left = (int)MathF.Floor(Math.Clamp(minX, 0, worldWidth - 1)) / ChunkSize;
        int right = (int)MathF.Floor(Math.Clamp(maxX, 0, worldWidth - 1)) / ChunkSize;
        int top = (int)MathF.Floor(Math.Clamp(minY, 0, worldHeight - 1)) / ChunkSize;
        int bottom = (int)MathF.Floor(Math.Clamp(maxY, 0, worldHeight - 1)) / ChunkSize;
        for (int x = left; x <= right; x++)
            for (int y = top; y <= bottom; y++)
                requiredChunks.Add((x, y));
    }

    public void Clear()
    {
        foreach (CollisionChunk chunk in chunks.Values)
            RemoveBody(chunk);
        chunks.Clear();
        requiredChunks.Clear();
        expiredChunks.Clear();
    }

    private void RemoveBody(CollisionChunk chunk)
    {
        if (chunk.Body.index1 == 0)
            return;
        B2Bodies.b2DestroyBody(chunk.Body);
        chunk.Body = default;
        BodyCount--;
        ShapeCount -= chunk.ShapeCount;
        chunk.ShapeCount = 0;
    }

    private void Rebuild(B2WorldId world, CollisionChunk chunk, int originX, int originY)
    {
        B2BodyId replacement = default;
        int shapeCount = 0;
        B2ShapeDef shapeDef = B2Types.b2DefaultShapeDef();
        // Terrain has no mass and needs no contact/event notifications of its own.
        shapeDef.density = 0;
        shapeDef.updateBodyMass = false;
        shapeDef.invokeContactCreation = true;
        try {
            if (Array.Exists(scanBuffer, static shape => shape != TerrainTileShape.None)) {
                B2BodyDef bodyDef = B2Types.b2DefaultBodyDef();
                bodyDef.type = B2BodyType.b2_staticBody;
                bodyDef.position = new B2Vec2(originX, originY);
                replacement = B2Bodies.b2CreateBody(world, bodyDef);
                shapeCount = TileCollisionBuilder.CreateShapes(replacement, ChunkSize, ChunkSize,
                    scanBuffer, merged, shapeDef);
            }
        }
        catch {
            if (replacement.index1 != 0)
                B2Bodies.b2DestroyBody(replacement);
            throw;
        }

        // Commit only after every new shape exists. A failed rebuild preserves
        // the previous body and snapshot so a later update can retry.
        RemoveBody(chunk);
        chunk.Body = replacement;
        chunk.ShapeCount = shapeCount;
        if (replacement.index1 != 0) {
            BodyCount++;
            ShapeCount += shapeCount;
        }
        scanBuffer.CopyTo(chunk.Shapes, 0);
    }

    private sealed class CollisionChunk
    {
        public readonly TerrainTileShape[] Shapes = new TerrainTileShape[ChunkArea];
        public B2BodyId Body;
        public int ShapeCount;
    }
}
