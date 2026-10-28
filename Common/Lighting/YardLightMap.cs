using System;
using Microsoft.Xna.Framework;
using Terraria.Graphics.Light;
using TerrariaDynamics.Common.TileYards;

namespace TerrariaDynamics.Common.Lighting;

internal readonly record struct YardLightSample(Vector3 Emission, LightMaskMode Mask);

// A separate vanilla propagation map for each Yard. Its one-cell empty border
// belongs to this map only; it never reads surrounding world/storage tiles.
internal sealed class YardLightMap(int width, int height)
{
    private readonly LightMap map = CreateMap(width, height);

    private static LightMap CreateMap(int width, int height)
    {
        if (width < 1 || height < 1 || (long)width * height > Yard.MaxSelectionArea)
            throw new ArgumentOutOfRangeException(nameof(width));
        var result = new LightMap { NonVisiblePadding = 0 };
        result.SetSize(checked(width + 2), checked(height + 2));
        return result;
    }

    public void Update(Func<int, int, YardLightSample> sample, Action<YardLightMap> injectBoundary = null)
    {
        map.Clear();
        for (int x = 0; x < width; x++) {
            for (int y = 0; y < height; y++) {
                YardLightSample cell = sample(x, y);
                map[x + 1, y + 1] = Sanitize(cell.Emission);
                map.SetMaskAt(x + 1, y + 1, cell.Mask);
            }
        }
        injectBoundary?.Invoke(this);
        map.Blur();
    }

    public void MergeLight(int localX, int localY, Vector3 light)
    {
        if (localX < 0 || localY < 0 || localX >= width || localY >= height)
            throw new ArgumentOutOfRangeException(nameof(localX));
        map[localX + 1, localY + 1] = Vector3.Max(map[localX + 1, localY + 1], Sanitize(light));
    }

    public Vector3 GetLight(int localX, int localY) =>
        localX >= 0 && localY >= 0 && localX < width && localY < height
            ? map[localX + 1, localY + 1] : Vector3.Zero;

    private static Vector3 Sanitize(Vector3 value) => new(
        float.IsFinite(value.X) ? Math.Clamp(value.X, 0, 10) : 0,
        float.IsFinite(value.Y) ? Math.Clamp(value.Y, 0, 10) : 0,
        float.IsFinite(value.Z) ? Math.Clamp(value.Z, 0, 10) : 0);
}
