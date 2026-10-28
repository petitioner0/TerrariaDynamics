using System;
using Box2D.NET;

namespace TerrariaDynamics.Common.Physics;

internal static class TileCollisionBuilder
{
    public static int CreateShapes(B2BodyId body, int width, int height,
        ReadOnlySpan<TerrainTileShape> shapes, Span<bool> merged, in B2ShapeDef definition)
    {
        B2ShapeDef shapeDefinition = definition;
        return BuildPolygons(width, height, shapes, merged,
            polygon => B2Shapes.b2CreatePolygonShape(body, shapeDefinition, polygon));
    }

    // Player queries and simulated bodies share the same merged geometry.
    public static int BuildPolygons(int width, int height, ReadOnlySpan<TerrainTileShape> shapes,
        Span<bool> merged, Action<B2Polygon> accept)
    {
        if (width <= 0 || height <= 0 || (long)width * height != shapes.Length || merged.Length != shapes.Length)
            throw new ArgumentException("Invalid collision grid dimensions.");
        merged.Clear();
        int count = 0;
        for (int y = 0; y < height; y++) {
            for (int x = 0; x < width; x++) {
                int index = y * width + x;
                TerrainTileShape shape = shapes[index];
                if (shape == TerrainTileShape.None || merged[index])
                    continue;
                B2Polygon polygon;
                if (shape == TerrainTileShape.Solid) {
                    int rectangleWidth = 1;
                    while (x + rectangleWidth < width && !merged[index + rectangleWidth]
                        && shapes[index + rectangleWidth] == TerrainTileShape.Solid)
                        rectangleWidth++;
                    int rectangleHeight = 1;
                    while (y + rectangleHeight < height) {
                        bool solidRow = true;
                        for (int column = x; column < x + rectangleWidth; column++) {
                            int candidate = (y + rectangleHeight) * width + column;
                            if (merged[candidate] || shapes[candidate] != TerrainTileShape.Solid) {
                                solidRow = false;
                                break;
                            }
                        }
                        if (!solidRow)
                            break;
                        rectangleHeight++;
                    }
                    for (int row = y; row < y + rectangleHeight; row++)
                        merged.Slice(row * width + x, rectangleWidth).Fill(true);
                    polygon = TileCollisionGeometry.Rectangle(x, y, rectangleWidth, rectangleHeight);
                }
                else
                    polygon = TileCollisionGeometry.Tile(shape, x, y);
                accept(polygon);
                count++;
            }
        }
        return count;
    }
}
