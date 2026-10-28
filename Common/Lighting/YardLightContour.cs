using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using TerrariaDynamics.Common.TileYards;

namespace TerrariaDynamics.Common.Lighting;

internal readonly record struct YardLightFace(Point Cell, Vector2 Center, Vector2 Normal);

// Eight triangles around each tile's center represent full blocks, half blocks
// and all four slopes exactly. Flood only empty triangles reachable from outside;
// enclosed cavities never become external light inputs.
internal sealed class YardLightContour
{
    private static readonly Point[] Rim = {
        new(0, 0), new(8, 0), new(16, 0), new(16, 8),
        new(16, 16), new(8, 16), new(0, 16), new(0, 8)
    };
    private static readonly Point[] Neighbors = {
        new(0, -1), new(0, -1), new(1, 0), new(1, 0),
        new(0, 1), new(0, 1), new(-1, 0), new(-1, 0)
    };
    private static readonly int[] Opposite = { 5, 4, 7, 6, 1, 0, 3, 2 };
    private readonly byte[] shapes;
    private readonly int height;
    public IReadOnlyList<YardLightFace> Faces { get; }

    private YardLightContour(byte[] shapes, int height, List<YardLightFace> faces)
    {
        this.shapes = shapes;
        this.height = height;
        Faces = faces;
    }

    // This describes the content silhouette, not light attenuation. Walls and
    // liquids can receive ambient light; wires/actuators do not enclose space.
    // Vanilla masks still decide propagation through glass, furniture and solids.
    public static byte ShapeOf(Tile tile)
    {
        if (!tile.HasTile || tile.IsActuated)
            return tile.WallType != WallID.None || tile.LiquidAmount > 0 ? (byte)255 : (byte)0;
        if (tile.IsHalfBlock)
            return 0x78;
        return tile.Slope switch {
            SlopeType.SlopeDownRight => 0x3c,
            SlopeType.SlopeDownLeft => 0xf0,
            SlopeType.SlopeUpRight => 0x0f,
            SlopeType.SlopeUpLeft => 0xc3,
            _ => 255
        };
    }

    public bool Matches(int x, int y, byte shape) => shapes[x * height + y] == shape;

    public static YardLightContour Build(int width, int height, Func<int, int, byte> shapeAt)
    {
        if (width < 1 || height < 1 || (long)width * height > Yard.MaxSelectionArea)
            throw new ArgumentOutOfRangeException(nameof(width));
        int count = width * height;
        var shapes = new byte[count];
        var exterior = new byte[count];
        var queue = new int[count * 8];
        int head = 0, tail = 0;
        for (int x = 0; x < width; x++)
            for (int y = 0; y < height; y++)
                shapes[x * height + y] = shapeAt(x, y);

        void Visit(int cell, int triangle)
        {
            int bit = 1 << triangle;
            if (((shapes[cell] | exterior[cell]) & bit) != 0)
                return;
            exterior[cell] |= (byte)bit;
            queue[tail++] = cell * 8 + triangle;
        }

        // Seed every open half-edge on the rectangular domain boundary.
        for (int x = 0; x < width; x++) {
            Visit(x * height, 0); Visit(x * height, 1);
            Visit(x * height + height - 1, 4); Visit(x * height + height - 1, 5);
        }
        for (int y = 0; y < height; y++) {
            Visit(y, 6); Visit(y, 7);
            Visit((width - 1) * height + y, 2); Visit((width - 1) * height + y, 3);
        }
        while (head < tail) {
            int entry = queue[head++];
            int cell = entry / 8, triangle = entry % 8;
            Visit(cell, (triangle + 7) % 8);
            Visit(cell, (triangle + 1) % 8);
            Point delta = Neighbors[triangle];
            int x = cell / height + delta.X, y = cell % height + delta.Y;
            if (x >= 0 && y >= 0 && x < width && y < height)
                Visit(x * height + y, Opposite[triangle]);
        }

        var faces = new List<YardLightFace>();
        var segments = new List<(Vector2 Start, Vector2 End)>(8);
        for (int x = 0; x < width; x++) {
            for (int y = 0; y < height; y++) {
                int cell = x * height + y;
                if (shapes[cell] == 0)
                    continue;
                segments.Clear();
                for (int triangle = 0; triangle < 8; triangle++) {
                    if ((shapes[cell] & (1 << triangle)) == 0)
                        continue;
                    Vector2 a = Rim[triangle].ToVector2();
                    Vector2 b = Rim[(triangle + 1) % 8].ToVector2();
                    if ((exterior[cell] & (1 << ((triangle + 7) % 8))) != 0)
                        segments.Add((new Vector2(8), a));
                    if ((exterior[cell] & (1 << ((triangle + 1) % 8))) != 0)
                        segments.Add((b, new Vector2(8)));
                    Point delta = Neighbors[triangle];
                    int nx = x + delta.X, ny = y + delta.Y;
                    if (nx < 0 || ny < 0 || nx >= width || ny >= height
                        || (exterior[nx * height + ny] & (1 << Opposite[triangle])) != 0)
                        segments.Add((a, b));
                }
                // Join collinear half-edges belonging to the same tile. A full
                // isolated tile therefore has four faces, not eight samples.
                for (int i = 0; i < segments.Count; i++) {
                    for (int j = i + 1; j < segments.Count; j++) {
                        var first = segments[i];
                        var second = segments[j];
                        Vector2 d1 = first.End - first.Start, d2 = second.End - second.Start;
                        if (d1.X * d2.Y != d1.Y * d2.X || Vector2.Dot(d1, d2) <= 0)
                            continue;
                        if (first.End == second.Start)
                            segments[i] = (first.Start, second.End);
                        else if (second.End == first.Start)
                            segments[i] = (second.Start, first.End);
                        else
                            continue;
                        segments.RemoveAt(j);
                        j = i;
                    }
                }
                foreach (var edge in segments) {
                    Vector2 direction = edge.End - edge.Start;
                    Vector2 normal = Vector2.Normalize(new Vector2(direction.Y, -direction.X));
                    faces.Add(new YardLightFace(new Point(x, y), new Vector2(x * 16, y * 16)
                        + (edge.Start + edge.End) * 0.5f, normal));
                }
            }
        }
        return new YardLightContour(shapes, height, faces);
    }

    public int Sample(YardCoordinates coordinates, Func<Vector2, Vector3> sampleWorld,
        Action<int, int, Vector3> merge)
    {
        int sampled = 0;
        foreach (YardLightFace face in Faces) {
            Vector2 normal = coordinates.LocalVectorToWorld(face.Normal);
            float weight = Math.Max(0, -normal.Y);
            // Ignore floating-point residue at exact quarter turns.
            if (weight <= 0.00001f)
                continue;
            Vector2 position = coordinates.LocalToWorld(face.Center) + normal * 8;
            merge(face.Cell.X, face.Cell.Y, sampleWorld(position) * weight);
            sampled++;
        }
        return sampled;
    }
}
