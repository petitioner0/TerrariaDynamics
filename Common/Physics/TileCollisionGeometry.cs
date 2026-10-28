using System;
using Box2D.NET;

namespace TerrariaDynamics.Common.Physics;

internal static class TileCollisionGeometry
{
    private static readonly B2Rot IdentityRotation = new(1, 0);

    public static B2Polygon Rectangle(float x, float y, float width, float height) =>
        B2Geometries.b2MakeOffsetBox(width / 2, height / 2,
            new B2Vec2(x + width / 2, y + height / 2), IdentityRotation);

    // Coordinates retain Terraria's downward-positive Y axis. Each tile is 1x1.
    public static B2Polygon Tile(TerrainTileShape shape, int x, int y)
    {
        if (shape == TerrainTileShape.Solid)
            return Rectangle(x, y, 1, 1);
        if (shape == TerrainTileShape.HalfBlock)
            return Rectangle(x, y + 0.5f, 1, 0.5f);

        Span<B2Vec2> vertices = stackalloc B2Vec2[3];
        switch (shape) {
            case TerrainTileShape.DownRight:
                vertices[0] = new B2Vec2(x + 1, y);
                vertices[1] = new B2Vec2(x + 1, y + 1);
                vertices[2] = new B2Vec2(x, y + 1);
                break;
            case TerrainTileShape.DownLeft:
                vertices[0] = new B2Vec2(x, y);
                vertices[1] = new B2Vec2(x + 1, y + 1);
                vertices[2] = new B2Vec2(x, y + 1);
                break;
            case TerrainTileShape.UpRight:
                vertices[0] = new B2Vec2(x, y);
                vertices[1] = new B2Vec2(x + 1, y);
                vertices[2] = new B2Vec2(x + 1, y + 1);
                break;
            case TerrainTileShape.UpLeft:
                vertices[0] = new B2Vec2(x, y);
                vertices[1] = new B2Vec2(x + 1, y);
                vertices[2] = new B2Vec2(x, y + 1);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(shape));
        }
        B2Hull hull = B2Hulls.b2ComputeHull(vertices, vertices.Length);
        return B2Geometries.b2MakePolygon(hull, 0);
    }
}
