using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Terraria;
using TerrariaDynamics.Common.TileYards;

namespace TerrariaDynamics.Common.Lighting;

internal static class YardLighting
{
    private const ulong UpdateInterval = 4;
    private static readonly Dictionary<Guid, Cache> caches = new();
    private static YardLightScanner scanner;

    private sealed class Cache(Yard region)
    {
        public readonly Yard Region = region;
        public readonly YardLightMap Map = new(region.Width, region.Height);
        public ulong LastUpdate;
        public bool Ready;
        public bool ShowInvisible;
    }

    public static Color GetColor(Yard region, int storageX, int storageY)
    {
        if (!region.IsLoaded || !region.Bounds.Contains(storageX, storageY))
            return Color.Black;
        if (!caches.TryGetValue(region.Id, out Cache cache)) {
            cache = new Cache(region);
            caches.Add(region.Id, cache);
        }
        ulong tick = Main.GameUpdateCount;
        bool showInvisible = Main.ShouldShowInvisibleWalls();
        if (!cache.Ready || tick - cache.LastUpdate >= UpdateInterval || cache.ShowInvisible != showInvisible) {
            scanner ??= new YardLightScanner();
            scanner.SetVisibility(showInvisible);
            if (region.LightContour == null)
                CaptureContour(region, fromSource: false);
            bool changed = false;
            YardCoordinates coordinates = region.RenderCoordinates;
            cache.Map.Update((x, y) => {
                changed |= !region.LightContour.Matches(x, y,
                    YardLightContour.ShapeOf(Main.tile[region.Bounds.X + x, region.Bounds.Y + y]));
                return scanner.Sample(Main.tile, region.Bounds, x, y, tick);
            }, map => {
                if (changed)
                    CaptureContour(region, fromSource: false);
                region.LightContour.Sample(coordinates, SampleWorld, map.MergeLight);
            });
            cache.LastUpdate = tick;
            cache.ShowInvisible = showInvisible;
            cache.Ready = true;
        }
        return ReadColor(cache, storageX, storageY);
    }

    // Vanilla GetTileDrawData samples lighting internally for a few glow layers.
    // Serve only already prepared Yard caches; never trigger a scan from this hook.
    public static bool TryGetCachedColor(int x, int y, out Color color)
    {
        if (x >= Main.maxTilesX) {
            foreach (Cache cache in caches.Values) {
                if (cache.Ready && cache.Region.IsLoaded && cache.Region.Bounds.Contains(x, y)) {
                    color = ReadColor(cache, x, y);
                    return true;
                }
            }
        }
        color = default;
        return false;
    }

    private static Color ReadColor(Cache cache, int storageX, int storageY)
    {
        Point local = cache.Region.Coordinates.StorageToLocalTile(new Point(storageX, storageY));
        // Apply display brightness once, after raw internal/world RGB were merged.
        Vector3 light = Vector3.Clamp(cache.Map.GetLight(local.X, local.Y) * 1.2f, Vector3.Zero, Vector3.One);
        return new Color(light);
    }

    public static void Remove(Guid id) => caches.Remove(id);

    public static void CaptureContour(Yard region, bool fromSource)
    {
        Rectangle bounds = fromSource ? region.Source : region.Bounds;
        region.LightContour = YardLightContour.Build(region.Width, region.Height,
            (x, y) => YardLightContour.ShapeOf(Main.tile[bounds.X + x, bounds.Y + y]));
    }

    private static Vector3 SampleWorld(Vector2 position)
    {
        if (!float.IsFinite(position.X) || !float.IsFinite(position.Y)
            || position.X < 0 || position.Y < 0
            || position.X >= Main.maxTilesX * 16f || position.Y >= Main.maxTilesY * 16f)
            return Vector3.Zero;
        // Raw interpolated engine RGB: no display brightness multiplication and
        // no call through the GetColor hook that serves our own Yard caches.
        return Terraria.Lighting.GetSubLight(position);
    }

    public static void Clear()
    {
        caches.Clear();
        scanner = null;
    }
}
