using System;
using Microsoft.Xna.Framework;

namespace TerrariaDynamics.Common.TileYards;

// Tile indices are integers; all transforms operate on PIXELS. Storage is fixed
// and axis-aligned. WorldOrigin is the transformed local top-left, in world pixels.
public readonly struct YardCoordinates(Point storageOrigin, Point size, Vector2 worldOrigin, float rotation)
{
    public const int TileSize = 16;
    private readonly float sin = MathF.Sin(rotation);
    private readonly float cos = MathF.Cos(rotation);
    public Point StorageOrigin { get; } = storageOrigin;
    public Point Size { get; } = size;
    public Vector2 WorldOrigin { get; } = worldOrigin;
    public float Rotation { get; } = rotation;
    public Rectangle StorageBounds => new(StorageOrigin.X, StorageOrigin.Y, Size.X, Size.Y);

    public Point LocalTileToStorage(Point tile) => new(tile.X + StorageOrigin.X, tile.Y + StorageOrigin.Y);
    public Point StorageToLocalTile(Point tile) => new(tile.X - StorageOrigin.X, tile.Y - StorageOrigin.Y);
    public Vector2 StorageToLocalPixels(int x, int y) => new((x - StorageOrigin.X) * TileSize, (y - StorageOrigin.Y) * TileSize);
    public Vector2 LocalToWorld(Vector2 pixels) => WorldOrigin + LocalVectorToWorld(pixels);
    public Vector2 WorldToLocal(Vector2 pixels) => WorldVectorToLocal(pixels - WorldOrigin);
    public Vector2 LocalVectorToWorld(Vector2 vector) => new(vector.X * cos - vector.Y * sin, vector.X * sin + vector.Y * cos);
    public Vector2 WorldVectorToLocal(Vector2 vector) => new(vector.X * cos + vector.Y * sin, -vector.X * sin + vector.Y * cos);
    public Vector2 StorageTileToWorld(int x, int y) => LocalToWorld(StorageToLocalPixels(x, y));
    public Point StorageTileToWorldCell(int x, int y) => PixelCell(LocalToWorld(StorageToLocalPixels(x, y) + new Vector2(8)));
    public Point WorldToStorageTile(Vector2 pixels) => LocalTileToStorage(PixelCell(WorldToLocal(pixels)));

    // Redeployment uses the local origin as its anchor, regardless of the visual
    // rotation. Round the origin to the nearest tile; half-tile ties go positive.
    public Point NearestWorldAnchorTile => PixelCell(WorldOrigin + new Vector2(TileSize / 2f));
    public Point StorageTileToPlacementCell(int x, int y)
    {
        Point anchor = NearestWorldAnchorTile;
        return new Point(anchor.X + x - StorageOrigin.X, anchor.Y + y - StorageOrigin.Y);
    }

    public bool TryWorldToStorageTile(Vector2 pixels, out Point storage)
    {
        storage = WorldToStorageTile(pixels);
        return StorageBounds.Contains(storage);
    }

    // Inverse-transform every viewport corner. This conservative local AABB also
    // includes crowns/sway extending past their owning cell, in any orientation.
    public Rectangle VisibleStorageArea(Vector2 worldMin, Vector2 worldMax, float overhang = 192)
    {
        Vector2 a = WorldToLocal(worldMin);
        Vector2 b = WorldToLocal(new Vector2(worldMax.X, worldMin.Y));
        Vector2 c = WorldToLocal(worldMax);
        Vector2 d = WorldToLocal(new Vector2(worldMin.X, worldMax.Y));
        Vector2 min = Vector2.Min(Vector2.Min(a, b), Vector2.Min(c, d)) - new Vector2(overhang);
        Vector2 max = Vector2.Max(Vector2.Max(a, b), Vector2.Max(c, d)) + new Vector2(overhang);
        int left = Math.Clamp((int)MathF.Floor(min.X / TileSize), 0, Size.X);
        int top = Math.Clamp((int)MathF.Floor(min.Y / TileSize), 0, Size.Y);
        int right = Math.Clamp((int)MathF.Ceiling(max.X / TileSize), 0, Size.X);
        int bottom = Math.Clamp((int)MathF.Ceiling(max.Y / TileSize), 0, Size.Y);
        return new Rectangle(StorageOrigin.X + left, StorageOrigin.Y + top,
            Math.Max(0, right - left), Math.Max(0, bottom - top));
    }

    private static Point PixelCell(Vector2 pixels) => new((int)MathF.Floor(pixels.X / TileSize), (int)MathF.Floor(pixels.Y / TileSize));
}
